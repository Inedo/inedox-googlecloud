using Inedo.Extensibility.FileSystems;

namespace Inedo.Extensions.GoogleCloud.FileSystems;

public sealed partial class GoogleCloudFileSystem
{
    private sealed class PrefixItem(string prefix) : FileSystemItem
    {
        public override string Name { get; } = prefix;
        public override long? Size => null;
        public override bool IsDirectory => true;
    }
}
