namespace Inedo.Extensions.GoogleCloud.Authentication;

internal sealed class JsonWebTokenHeader
{
    public required string Alg { get; init; }
    public required string Typ { get; init; }
}
