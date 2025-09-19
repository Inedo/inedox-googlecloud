namespace Inedo.Extensions.GoogleCloud.FileSystems;

public sealed partial class GoogleCloudFileSystem
{
    private sealed class TokenInfo(DateTime granted, long expiresIn, string token)
    {
        public DateTime Expiration { get; } = granted + TimeSpan.FromSeconds(expiresIn);
        public string Token { get; } = token;

        public bool IsExpired() => DateTime.UtcNow >= this.Expiration.Subtract(new TimeSpan(0, 5, 0));
    }
}
