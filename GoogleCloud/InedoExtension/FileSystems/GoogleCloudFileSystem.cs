using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Inedo.Documentation;
using Inedo.Extensibility.FileSystems;
using Inedo.Extensions.GoogleCloud.Authentication;
using Inedo.Extensions.GoogleCloud.Client;
using Inedo.IO;
using Inedo.Serialization;
using Inedo.Web;

namespace Inedo.Extensions.GoogleCloud.FileSystems;

[DisplayName("Google Cloud")]
[Description("A file system backed by Google Cloud Object Storage.")]
public sealed partial class GoogleCloudFileSystem : FileSystem
{
    private readonly Lazy<HttpClient> client;
    private readonly Lazy<TokenCacheKey> cacheKey;
    private readonly HashSet<string> virtualDirectories = [];
    private static readonly ConcurrentDictionary<TokenCacheKey, TokenInfo> currentTokens = [];

    public GoogleCloudFileSystem()
    {
        this.client = new Lazy<HttpClient>(
            () =>
            {
                var baseUrl = AH.CoalesceString(this.BaseUrl, "https://storage.googleapis.com");
                var client = SDK.CreateHttpClient();
                client.BaseAddress = new Uri($"{baseUrl.TrimEnd('/')}/");
                return client;
            }
        );

        this.cacheKey = new Lazy<TokenCacheKey>(() => new TokenCacheKey(this.ServiceAccountKey!));
    }

    [Persistent]
    [DisplayName("Bucket")]
    public string? BucketName { get; set; }
    [Persistent]
    [DisplayName("Prefix")]
    [PlaceholderText("none (use bucket root)")]
    public string? TargetPath { get; set; }
    [Persistent(Encrypted = true)]
    [DisplayName("Service account key")]
    [FieldEditMode(FieldEditMode.Multiline)]
    [Description("Paste the private key JSON object generated for the serivce account that ProGet will use to access the storage bucket.")]
    public string? ServiceAccountKey { get; set; }
    [Category("Advanced")]
    [PlaceholderText("https://storage.googleapis.com")]
    public string? BaseUrl { get; set; }

    private string EncodedBucket() => !string.IsNullOrEmpty(this.BucketName) ? Uri.EscapeDataString(this.BucketName) : throw new InvalidOperationException("Bucket name property is required.");

    public override async Task MoveFileAsync(string originalName, string newName, bool overwrite, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref originalName);
        this.ResolvePath(ref newName);

        if (!overwrite && await this.FileExistsAsync(newName, cancellationToken))
            throw new InvalidOperationException($"{newName} already exists and overwrite was false.");

        using var request = await this.GetRequestAsync($"storage/v1/b/{this.EncodedBucket()}/o/{Uri.EscapeDataString(originalName)}/moveTo/o/{Uri.EscapeDataString(newName)}", cancellationToken);
        request.Method = HttpMethod.Post;
        using var response = await this.GetResponseAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }
    public override async Task CopyFileAsync(string sourceName, string targetName, bool overwrite, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref sourceName);
        this.ResolvePath(ref targetName);

        if (!overwrite && await this.FileExistsAsync(targetName, cancellationToken))
            throw new InvalidOperationException($"{targetName} already exists and overwrite was false.");

        RewriteResponse? response = null;
        do
        {
            response = await this.GetFromJsonAsync(
                HttpMethod.Post,
                getUrl(response?.RewriteToken),
                GoogleCloudJsonContext.Default.RewriteResponse,
                cancellationToken
            );
        }
        while (!response.Done && !string.IsNullOrEmpty(response.RewriteToken));

        string getUrl(string? rewriteToken)
        {
            var url = $"storage/v1/b/{this.EncodedBucket()}/o/{Uri.EscapeDataString(sourceName)}/rewriteTo/b/{this.EncodedBucket()}/o/{Uri.EscapeDataString(targetName)}";
            if (!string.IsNullOrEmpty(rewriteToken))
                url = $"{url}&rewriteToken={Uri.EscapeDataString(rewriteToken)}";
            return url;
        }
    }
    public override Task CreateDirectoryAsync(string directoryName, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref directoryName);

        // Even when hierarhical naming is enabled, there isn't a reason to explicitly create a folder since
        // it will get created automatically as necessary when an object is added

        if (!string.IsNullOrEmpty(directoryName))
        {
            if (this.virtualDirectories.Add(directoryName))
            {
                var parts = directoryName.Split('/');
                for (int i = 1; i < parts.Length; i++)
                    this.virtualDirectories.Add(string.Join('/', parts.Take(i)));
            }
        }

        return Task.CompletedTask;
    }
    public override async Task<Stream> CreateFileAsync(string fileName, FileAccessHints hints = FileAccessHints.Default, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref fileName);

        var request = await this.GetRequestAsync($"upload/storage/v1/b/{this.EncodedBucket()}/o?name={Uri.EscapeDataString(fileName)}&uploadType=media", cancellationToken);
        request.Method = HttpMethod.Post;
        var pipe = new Pipe();
        request.Content = new StreamContent(pipe.Reader.AsStream());
        var task = this.client.Value.SendAsync(request, cancellationToken);
        return new CreateFileStream(pipe, task);
    }
    public override async Task DeleteDirectoryAsync(string directoryName, bool recursive, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref directoryName);

        if (!recursive)
            return;

        var toDelete = new List<FileSystemItem>();

        await foreach (var item in this.ListContentsAsync(directoryName, true, cancellationToken))
        {
            if (!item.IsDirectory)
                toDelete.Add(item);
        }

        foreach (var item in toDelete)
            await this.DeleteFileAsync(getFullPath(item.Name), cancellationToken);

        string getFullPath(string itemPath) => string.IsNullOrEmpty(directoryName) ? itemPath : $"{directoryName.TrimEnd('/')}/{itemPath}";
    }
    public override async Task DeleteFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref fileName);

        using var request = await this.GetRequestAsync($"storage/v1/b/{this.EncodedBucket()}/o/{Uri.EscapeDataString(fileName)}", cancellationToken);
        request.Method = HttpMethod.Delete;
        using var response = await this.client.Value.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;

        await EnsureSuccessAsync(response, cancellationToken);
    }
    public override async Task<FileSystemItem?> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref path);

        using var request = await this.GetRequestAsync($"storage/v1/b/{this.EncodedBucket()}/o/{Uri.EscapeDataString(path)}", cancellationToken);
        using var response = await this.client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);

        var obj = await response.Content.ReadFromJsonAsync(GoogleCloudJsonContext.Default.StorageObject, cancellationToken);
        if (obj is null)
            return null;

        return new ObjectItem(obj, PathEx.GetFileName(path)!);
    }
    public override async ValueTask<long?> GetDirectoryContentSizeAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref path);

        long size = 0;
        await foreach (var item in this.ListContentsAsync(path, recursive, cancellationToken))
            size += item.Size.GetValueOrDefault();
        return size;
    }
    public override IAsyncEnumerable<FileSystemItem> ListContentsAsync(string path, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref path);
        return this.ListContentsAsync(path, false, cancellationToken);
    }

    public override async Task<Stream?> OpenReadAsync(string fileName, FileAccessHints hints = FileAccessHints.Default, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref fileName);

        if (hints.HasFlag(FileAccessHints.RandomAccess))
        {
            using var request = await this.GetRequestAsync($"storage/v1/b/{this.EncodedBucket()}/o/{Uri.EscapeDataString(fileName)}", cancellationToken);
            using var response = await this.client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            await EnsureSuccessAsync(response, cancellationToken);

            var obj = await response.Content.ReadFromJsonAsync(GoogleCloudJsonContext.Default.StorageObject, cancellationToken);
            if (obj is null)
                return null;

            return new BufferedStream(new SeekableDownloadStream(fileName, obj.Generation, this.client.Value, obj.Size));
        }
        else
        {
            using var request = await this.GetRequestAsync($"storage/v1/b/{this.EncodedBucket()}/o/{Uri.EscapeDataString(fileName)}?alt=media", cancellationToken);
            var response = await this.client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                response.Dispose();
                return null;
            }

            await EnsureSuccessAsync(response, cancellationToken);

            return new DisposingStream(await response.Content.ReadAsStreamAsync(cancellationToken), response);
        }
    }
    public override async Task<UploadStream> BeginResumableUploadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref fileName);

        using var request = await this.GetRequestAsync($"upload/storage/v1/b/{this.EncodedBucket()}/o?name={Uri.EscapeDataString(fileName)}&uploadType=resumable", cancellationToken);
        request.Method = HttpMethod.Post;

        using var response = await this.GetResponseAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var uploadUri = response.Headers.Location ?? throw new InvalidDataException("Expected Location header to be set in response for resumable upload.");
        return new ResumableUploadStream(uploadUri.ToString(), this, 0);
    }
    public override Task<UploadStream> ContinueResumableUploadAsync(string fileName, byte[] state, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref fileName);

        var (uploadUrl, baseOffset, remainder) = ReadUploadState(state);
        int remainderSize = (int)baseOffset % ResumableUploadStream.ChunkMultipleSize;

        var stream = new ResumableUploadStream(uploadUrl, this, baseOffset - remainderSize);
        if (remainderSize > 0)
        {
            using var brotli = new BrotliStream(new ReadOnlyMemoryStream(remainder), CompressionMode.Decompress);
            brotli.CopyTo(stream);
        }

        return Task.FromResult<UploadStream>(stream);
    }
    public override async Task CancelResumableUploadAsync(string fileName, byte[] state, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref fileName);

        var (uploadUrl, _, _) = ReadUploadState(state);
        using var request = await this.GetRequestAsync(uploadUrl, cancellationToken);
        request.Method = HttpMethod.Delete;
        using var response = await this.client.Value.SendAsync(request, cancellationToken);
        // note that we purposefully do not check the status code here because it always indicates an error and there is very little we can do if the cancel doesn't work anyway
    }
    public override async Task CompleteResumableUploadAsync(string fileName, byte[] state, CancellationToken cancellationToken = default)
    {
        this.ResolvePath(ref fileName);

        var (uploadUrl, baseOffset, remainder) = ReadUploadState(state);

        using var request = await this.GetRequestAsync(uploadUrl, cancellationToken);
        request.Method = HttpMethod.Put;

        int remainderSize = (int)baseOffset % ResumableUploadStream.ChunkMultipleSize;
        if (remainderSize > 0)
        {
            long actualBaseOffset = baseOffset - remainderSize;
            request.Content = new StreamContent(new BrotliStream(new ReadOnlyMemoryStream(remainder), CompressionMode.Decompress));
            request.Content.Headers.Add("Content-Range", $"bytes {actualBaseOffset}-{baseOffset - 1}/{baseOffset}");
        }
        else
        {
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.Add("Content-Range", $"*/{baseOffset}");
        }

        using var response = await this.GetResponseAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    public override RichDescription GetDescription()
    {
        if (string.IsNullOrEmpty(this.BucketName))
            return base.GetDescription();

        return new RichDescription(
            "Google Cloud Storage: ",
            new Hilite($"{this.BucketName}://{this.TargetPath?.TrimStart('/')}")
        );
    }

    private static (string uploadUrl, long baseOffset, ReadOnlyMemory<byte> remainder) ReadUploadState(byte[] state)
    {
        using var reader = new BinaryReader(new MemoryStream(state, false));
        long baseOffset = reader.Read7BitEncodedInt64();
        var uploadUrl = reader.ReadString();
        return (uploadUrl, baseOffset, state.AsMemory((int)reader.BaseStream.Position));
    }
    private async IAsyncEnumerable<FileSystemItem> ListContentsAsync(string path, bool recursive, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(path) && !path.EndsWith('/'))
            path = $"{path}/";

        var url = !recursive ? $"storage/v1/b/{this.EncodedBucket()}/o?delimiter=%2F&prefix={Uri.EscapeDataString(path)}" : $"storage/v1/b/{this.EncodedBucket()}/o?prefix={Uri.EscapeDataString(path)}";
        while (true)
        {
            var response = await this.GetFromJsonAsync(url, GoogleCloudJsonContext.Default.StorageObjectListResponse, cancellationToken);
            if (response is null)
                break;

            if (response.Prefixes is not null)
            {
                foreach (var prefix in response.Prefixes)
                {
                    if (prefix.Length > path.Length)
                        yield return new PrefixItem(prefix[path.Length..].TrimEnd('/'));
                }
            }

            if (response.Items is not null)
            {
                foreach (var item in response.Items)
                {
                    var name = getRelativePath(item.Name);
                    if (item.Size == 0 && name.EndsWith('/'))
                        yield return new PrefixItem(name.TrimEnd('/'));
                    else if (name.Length > 0)
                        yield return new ObjectItem(item, name);
                }
            }

            if (!string.IsNullOrEmpty(response.NextPageToken))
                url = $"storage/v1/b/{this.EncodedBucket()}/o?delimiter=%2F&prefix={Uri.EscapeDataString(path)}&pageToken={Uri.EscapeDataString(response.NextPageToken)}";
            else
                break;
        }

        string getRelativePath(string itemPath)
        {
            if (itemPath.StartsWith(path))
                return itemPath[path.Length..];
            else
                return itemPath;
        }
    }
    private async Task<T> GetFromJsonAsync<T>(HttpMethod method, string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var request = await this.GetRequestAsync(url, cancellationToken);
        request.Method = method;
        using var response = await this.GetResponseAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return (await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken)) ?? throw new InvalidDataException("Unexpected null token.");
    }
    private Task<T> GetFromJsonAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) => this.GetFromJsonAsync(HttpMethod.Get, url, typeInfo, cancellationToken);
    private async Task<HttpResponseMessage> GetResponseAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        var response = await this.client.Value.SendAsync(request, completionOption, cancellationToken);
        try
        {
            await EnsureSuccessAsync(response, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(message, null, response.StatusCode);
        }
    }
    private async Task<HttpRequestMessage> GetRequestAsync(string url, CancellationToken cancellationToken)
    {
        var client = this.client.Value;

        var key = this.cacheKey.Value;

        if (!currentTokens.TryGetValue(key, out var tokenInfo) || tokenInfo.IsExpired())
        {
            var (jwt, tokenUri) = this.GetJwt();

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri);
            request.Content = new FormUrlEncodedContent(
                [
                    KeyValuePair.Create("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                    KeyValuePair.Create("assertion", jwt)
                ]
            );

            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(errorText, null, response.StatusCode);
            }

            var token = (await response.Content.ReadFromJsonAsync(GoogleCloudAuthenticationJsonContext.Default.JsonWebTokenResponse, cancellationToken))!;
            tokenInfo = new TokenInfo(DateTime.UtcNow, token.ExpiresIn, token.AccessToken);
            currentTokens[key] = tokenInfo;
        }

        var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenInfo.Token);
        return requestMessage;
    }
    private (string jwt, string tokenUri) GetJwt()
    {
        var key = JsonSerializer.Deserialize(this.ServiceAccountKey!, GoogleCloudAuthenticationJsonContext.Default.ServiceAccountKey)!;

        var now = DateTime.UtcNow;
        var unixNow = (long)now.Subtract(DateTime.UnixEpoch).TotalSeconds;

        var header = new JsonWebTokenHeader { Alg = "RS256", Typ = "JWT" };
        var payload = new JsonWebTokenPayload
        {
            Iss = key.ClientEmail,
            Scope = "https://www.googleapis.com/auth/cloud-platform",
            Aud = key.TokenUri,
            Iat = unixNow,
            Exp = unixNow + 3600
        };

        var headerText = JsonSerializer.SerializeToUtf8Bytes(header, GoogleCloudAuthenticationJsonContext.Default.JsonWebTokenHeader);
        var payloadText = JsonSerializer.SerializeToUtf8Bytes(payload, GoogleCloudAuthenticationJsonContext.Default.JsonWebTokenPayload);

        var signaturePayloadText = $"{encode(headerText)}.{encode(payloadText)}";
        var signaturePayloadBytes = Encoding.ASCII.GetBytes(signaturePayloadText);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKey);

        var signatureBytes = rsa.SignData(signaturePayloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return ($"{signaturePayloadText}.{encode(signatureBytes)}", key.TokenUri);

        static string encode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }

    private void ResolvePath([NotNull] ref string? path)
    {
        var p = string.IsNullOrEmpty(this.TargetPath) ? path : $"{this.TargetPath}/{path}";
        path = CleanSeparatorRegex().Replace(p ?? string.Empty, "/").Trim('/');
    }

    [GeneratedRegex(@"/{2,}|\\+")]
    private static partial Regex CleanSeparatorRegex();
}
