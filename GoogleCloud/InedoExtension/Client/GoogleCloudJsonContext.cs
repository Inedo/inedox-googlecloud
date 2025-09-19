using System.Text.Json.Serialization;

namespace Inedo.Extensions.GoogleCloud.Client;

[JsonSerializable(typeof(BucketResource))]
[JsonSerializable(typeof(RewriteResponse))]
[JsonSerializable(typeof(StorageObjectListResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
internal sealed partial class GoogleCloudJsonContext : JsonSerializerContext
{
}
