namespace Inedo.Extensions.GoogleCloud.Client;

internal class PagedResponse<TItem>
{
    public string? NextPageToken { get; init; }
    public TItem[]? Items { get; init; }
}
