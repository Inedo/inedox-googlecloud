using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Inedo.Extensions.GoogleCloud.FileSystems;

public sealed partial class GoogleCloudFileSystem
{
    private readonly struct TokenCacheKey(string serviceKey) : IEquatable<TokenCacheKey>
    {
        public byte[] Hash { get; } = serviceKey is not null ? SHA256.HashData(Encoding.UTF8.GetBytes(serviceKey)) : [];

        public bool Equals(TokenCacheKey other)
        {
            if (ReferenceEquals(this.Hash, other.Hash))
                return true;
            if (this.Hash is null || other.Hash is null)
                return false;

            return this.Hash.AsSpan().SequenceEqual(other.Hash);
        }
        public override bool Equals([NotNullWhen(true)] object? obj) => obj is TokenCacheKey other && this.Equals(other);
        public override int GetHashCode() => BinaryPrimitives.ReadInt32LittleEndian(this.Hash);
        public override string ToString() => Convert.ToHexString(this.Hash);
    }
}
