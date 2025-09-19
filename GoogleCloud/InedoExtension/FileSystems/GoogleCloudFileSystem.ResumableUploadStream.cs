using System.IO.Compression;
using System.Net;
using Inedo.Extensibility.FileSystems;
using Inedo.IO;

namespace Inedo.Extensions.GoogleCloud.FileSystems;

public sealed partial class GoogleCloudFileSystem
{
    private sealed class ResumableUploadStream(string uploadUrl, GoogleCloudFileSystem fs, long baseOffset) : UploadStream
    {
        public const int ChunkMultipleSize = 256 * 1024;
        private readonly GoogleCloudFileSystem fs = fs;
        private readonly string uploadUrl = uploadUrl;
        private readonly TemporaryStream tempStream = new();
        private readonly long baseOffset = baseOffset;
        private bool disposed;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.tempStream.Write(buffer);
            this.IncrementBytesWritten(buffer.Length);
        }
        public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));
        public override void WriteByte(byte value)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.tempStream.WriteByte(value);
            this.IncrementBytesWritten(1);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            await this.tempStream.WriteAsync(buffer, cancellationToken);
            this.IncrementBytesWritten(buffer.Length);
        }

        public override async Task<byte[]?> CommitAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, InedoLib.UTF8Encoding, true))
            {
                writer.Write7BitEncodedInt64(this.baseOffset + this.tempStream.Length);
                writer.Write(this.uploadUrl);
            }

            int remainderSize = (int)(tempStream.Length % ChunkMultipleSize);
            if (remainderSize != 0)
            {
                this.tempStream.Seek(-remainderSize, SeekOrigin.End);

                using (var brotli = new BrotliStream(buffer, CompressionLevel.Optimal, true))
                {
                    this.tempStream.CopyTo(brotli);
                }

                this.tempStream.SetLength(this.tempStream.Length - remainderSize);
            }

            if (this.tempStream.Length > 0)
            {
                using var request = await this.fs.GetRequestAsync(this.uploadUrl, default);
                request.Method = HttpMethod.Put;
                this.tempStream.Position = 0;
                request.Content = new StreamContent(this.tempStream);
                request.Content.Headers.Add("Content-Range", $"bytes {this.baseOffset}-{this.baseOffset + this.tempStream.Length - 1}/*");
                using var response = await this.fs.client.Value.SendAsync(request, cancellationToken);
                if (response.StatusCode >= HttpStatusCode.BadRequest)
                    await EnsureSuccessAsync(response, cancellationToken);
            }

            return buffer.ToArray();
        }

        protected override void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                    this.tempStream.Dispose();

                this.disposed = true;
            }

            base.Dispose(disposing);
        }
    }
}
