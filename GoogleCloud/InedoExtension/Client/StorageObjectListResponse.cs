namespace Inedo.Extensions.GoogleCloud.Client;

internal sealed class StorageObjectListResponse : PagedResponse<StorageObject>
{
    public string[]? Prefixes { get; init; }
}
