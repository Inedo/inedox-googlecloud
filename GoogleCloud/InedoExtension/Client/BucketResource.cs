namespace Inedo.Extensions.GoogleCloud.Client;

internal sealed class BucketResource
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public EnabledContainer? HierarchicalNamespace { get; init; }
}
