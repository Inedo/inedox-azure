using Inedo.Extensibility.FileSystems;
using Inedo.IO;
using Microsoft.Azure.Storage.Blob;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Inedo.ProGet.Extensions.Azure.PackageStores
{
    internal sealed class BlobUploadStream : UploadStream
    {
        private readonly CloudBlockBlob blob;
        private readonly int chunkIndex;
        private readonly Lazy<TemporaryStream> tempStream = new();

        public BlobUploadStream(CloudBlockBlob blob, int chunkIndex = 0)
        {
            this.blob = blob;
            this.chunkIndex = chunkIndex;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0)
                return;

            this.tempStream.Value.Write(buffer, offset, count);
            this.IncrementBytesWritten(count);
        }
        public override void WriteByte(byte value)
        {
            this.tempStream.Value.WriteByte(value);
            this.IncrementBytesWritten(1);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (count == 0)
                return;

            await this.tempStream.Value.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            this.IncrementBytesWritten(count);
        }
        public override async Task<byte[]> CommitAsync(CancellationToken cancellationToken)
        {
            if (this.BytesWritten > 0)
            {
                int index = this.chunkIndex + 1;
                var data = BitConverter.GetBytes(index);
                this.tempStream.Value.Seek(0, SeekOrigin.Begin);
                await this.blob.PutBlockAsync(Convert.ToBase64String(data), this.tempStream.Value, null, cancellationToken).ConfigureAwait(false);
                return data;
            }
            else
            {
                return BitConverter.GetBytes(this.chunkIndex);
            }
        }
#if !NET452
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.IsEmpty)
                return;

            this.tempStream.Value.Write(buffer);
            this.IncrementBytesWritten(buffer.Length);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
                return;

            await this.tempStream.Value.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            this.IncrementBytesWritten(buffer.Length);
        }
        public override ValueTask DisposeAsync() => this.tempStream.IsValueCreated ? this.tempStream.Value.DisposeAsync() : default;
#endif

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (this.tempStream.IsValueCreated)
                    this.tempStream.Value.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
