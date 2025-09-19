namespace Inedo.Extensions.GoogleCloud.Client;

internal sealed class RewriteResponse
{
    public required string Kind { get; init; }
    public long TotalBytesRewritten { get; init; }
    public long ObjectSize { get; init; }
    public bool Done { get; init; }
    public string? RewriteToken { get; init; }
    public StorageObject? Resource { get; init; }
}
