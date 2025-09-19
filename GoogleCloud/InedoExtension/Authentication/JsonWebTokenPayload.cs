namespace Inedo.Extensions.GoogleCloud.Authentication;

internal sealed class JsonWebTokenPayload
{
    public required string Iss { get; init; }
    public required string Scope { get; init; }
    public required string Aud { get; init; }
    public required long Iat { get; init; }
    public required long Exp { get; init; }
}
