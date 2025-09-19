using System.Text.Json.Serialization;

namespace Inedo.Extensions.GoogleCloud.Authentication;

[JsonSerializable(typeof(ServiceAccountKey))]
[JsonSerializable(typeof(JsonWebTokenHeader))]
[JsonSerializable(typeof(JsonWebTokenPayload))]
[JsonSerializable(typeof(JsonWebTokenResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class GoogleCloudAuthenticationJsonContext : JsonSerializerContext
{
}
