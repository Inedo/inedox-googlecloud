namespace Inedo.Extensions.GoogleCloud.Client;

internal sealed class StorageObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required DateTime Updated { get; init; }
    public string? MediaLink { get; init; }
    public long Generation { get; init; }
}
