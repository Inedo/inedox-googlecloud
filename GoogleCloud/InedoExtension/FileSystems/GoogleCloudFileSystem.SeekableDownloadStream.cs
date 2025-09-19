using System.Net.Http.Headers;

namespace Inedo.Extensions.GoogleCloud.FileSystems;

public sealed partial class GoogleCloudFileSystem
{
    private sealed class SeekableDownloadStream(string fileName, long generation, HttpClient client, long size) : Stream
    {
        private readonly string fileName = fileName;
        private readonly long generation = generation;
        private readonly HttpClient client = client;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; } = size;
        public override long Position { get; set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (this.Position >= this.Length)
                return 0;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"o/{Uri.EscapeDataString(this.fileName)}&generation={this.generation}?alt=media");

            int length = (int)Math.Min(buffer.Length, this.Length - this.Position);
            request.Headers.Range = new RangeHeaderValue(this.Position, this.Position + length - 1);

            using var response = await this.client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await stream.ReadExactlyAsync(buffer[..length], cancellationToken);
            return length;
        }
        public override int Read(Span<byte> buffer)
        {
            if (this.Position >= this.Length)
                return 0;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"o/{Uri.EscapeDataString(this.fileName)}&generation={this.generation}?alt=media");

            int length = (int)Math.Min(buffer.Length, this.Length - this.Position);
            request.Headers.Range = new RangeHeaderValue(this.Position, length - 1);

            using var response = this.client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            using var stream = response.Content.ReadAsStream();
            stream.ReadExactly(buffer[..length]);
            return length;
        }
        public override int Read(byte[] buffer, int offset, int count) => this.Read(buffer.AsSpan(offset, count));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin)
        {
            return this.Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => this.Position + offset,
                SeekOrigin.End => this.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush()
        {
        }
    }
}
