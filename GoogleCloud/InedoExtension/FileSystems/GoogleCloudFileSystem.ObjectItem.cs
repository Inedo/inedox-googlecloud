using Inedo.Extensibility.FileSystems;
using Inedo.Extensions.GoogleCloud.Client;

namespace Inedo.Extensions.GoogleCloud.FileSystems;

public sealed partial class GoogleCloudFileSystem
{
    private sealed class ObjectItem(StorageObject obj, string name) : FileSystemItem
    {
        private readonly StorageObject obj = obj;

        public override string Name { get; } = name;
        public override long? Size => this.obj.Size;
        public override DateTimeOffset? LastModifyTime => this.obj.Updated;
        public override string? PublicUrl => this.obj.MediaLink;
        public override bool IsDirectory => false;
    }
}
