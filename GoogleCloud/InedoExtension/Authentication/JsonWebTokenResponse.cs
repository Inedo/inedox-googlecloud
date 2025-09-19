namespace Inedo.Extensions.GoogleCloud.Authentication;

internal sealed class JsonWebTokenResponse
{
    public required string AccessToken { get; init; }
    public required long ExpiresIn { get; init; }
    public required string TokenType { get; init; }
}
