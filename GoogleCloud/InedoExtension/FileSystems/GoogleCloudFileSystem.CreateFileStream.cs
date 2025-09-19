using System.Buffers;
using System.IO.Pipelines;

namespace Inedo.Extensions.GoogleCloud.FileSystems;

public sealed partial class GoogleCloudFileSystem
{
    private sealed class CreateFileStream(Pipe pipe, Task<HttpResponseMessage> getResponse) : Stream
    {
        private readonly Pipe pipe = pipe;
        private readonly Task<HttpResponseMessage> getResponseTask = getResponse;
        private bool disposed;
        private bool completed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            if (!this.completed)
                this.pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (!this.completed)
                return this.pipe.Writer.FlushAsync(cancellationToken).AsTask();
            else
                return Task.CompletedTask;
        }

        public override void WriteByte(byte value)
        {
            this.CheckCompletedOrDisposed();
            this.pipe.Writer.Write(new Span<byte>(ref value));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            this.CheckCompletedOrDisposed();
            this.pipe.Writer.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            this.CheckCompletedOrDisposed();
            await this.pipe.Writer.WriteAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                this.CompleteRequest().Dispose();
                this.disposed = true;
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (!this.disposed)
            {
                var response = await this.CompleteRequestAsync();
                await EnsureSuccessAsync(response, default);
                response.Dispose();
                this.disposed = true;
            }
        }

        private async Task<HttpResponseMessage> CompleteRequestAsync()
        {
            if (!this.completed)
            {
                this.completed = true;
                await this.pipe.Writer.CompleteAsync();
            }

            return await this.getResponseTask;
        }
        private HttpResponseMessage CompleteRequest()
        {
            if (!this.completed)
            {
                this.completed = true;
                this.pipe.Writer.Complete();
            }

            return this.getResponseTask.GetAwaiter().GetResult();
        }

        private void CheckCompletedOrDisposed()
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            if (this.completed)
                throw new InvalidOperationException($"{nameof(CreateFileStream)} has already been completed.");
        }
    }
}
