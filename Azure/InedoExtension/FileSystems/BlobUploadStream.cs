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
        private const long MaxChunkSize = 50 * 1024 * 1024;
        private readonly CloudBlockBlob blob;
        private int chunkIndex;
        private readonly object syncLock = new();
        private TemporaryStream writeStream = new();
        private Task uploadTask;
        private int writeByteCalls;
        private bool disposed;

        public BlobUploadStream(CloudBlockBlob blob, int chunkIndex = 0)
        {
            this.blob = blob;
            this.chunkIndex = chunkIndex;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            this.writeStream.Write(buffer, 0, count);
            this.IncrementBytesWritten(count);
            this.CheckAndBeginBackgroundUpload();
        }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await this.writeStream.WriteAsync(buffer, 0, count, cancellationToken).ConfigureAwait(false);
            this.IncrementBytesWritten(count);
            this.CheckAndBeginBackgroundUpload();
        }
        public override void WriteByte(byte value)
        {
            this.writeStream.WriteByte(value);
            this.IncrementBytesWritten(1);

            // don't do the check for every single byte in case this gets called a lot
            this.writeByteCalls++;
            if ((this.writeByteCalls % 1000) == 0)
                this.CheckAndBeginBackgroundUpload();
        }

        public override async Task<byte[]> CommitAsync(CancellationToken cancellationToken)
        {
            await this.CompleteUploadAsync().ConfigureAwait(false);
            return BitConverter.GetBytes(this.chunkIndex);
        }
#if !NET452
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            this.writeStream.Write(buffer);
            this.IncrementBytesWritten(buffer.Length);
            this.CheckAndBeginBackgroundUpload();
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await this.writeStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            this.IncrementBytesWritten(buffer.Length);
            this.CheckAndBeginBackgroundUpload();
        }
        public override async ValueTask DisposeAsync()
        {
            if (!this.disposed)
            {
                await this.CompleteUploadAsync().ConfigureAwait(false);
                await this.writeStream.DisposeAsync().ConfigureAwait(false);

                this.disposed = true;
            }
        }
#endif

        protected override void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    this.CompleteUploadAsync().GetAwaiter().GetResult();
                    this.writeStream.Dispose();
                }

                this.disposed = true;
            }

            base.Dispose(disposing);
        }

        private async Task CompleteUploadAsync()
        {
            Task waitTask;
            lock (this.syncLock)
            {
                waitTask = this.uploadTask;
            }

            if (waitTask != null)
                await waitTask.ConfigureAwait(false);

            if (this.writeStream.Position > 0)
            {
                this.writeStream.Position = 0;
                int index = this.chunkIndex;
                var data = BitConverter.GetBytes(index);
                await this.blob.PutBlockAsync(Convert.ToBase64String(data), this.writeStream).ConfigureAwait(false);
                this.chunkIndex++;
            }
        }
        private void CheckAndBeginBackgroundUpload()
        {
            if (this.writeStream.Position >= MaxChunkSize)
            {
                lock (this.syncLock)
                {
                    var stream = this.writeStream;
                    this.writeStream = new TemporaryStream();

                    stream.Position = 0;
                    int chunkIndex = this.chunkIndex++;

                    if (this.uploadTask == null)
                        this.uploadTask = Task.Run(() => this.UploadChunkAsync(stream, chunkIndex));
                    else
                        this.uploadTask = this.uploadTask.ContinueWith(_ => this.UploadChunkAsync(stream, chunkIndex)).Unwrap();
                }
            }
        }
        private async Task UploadChunkAsync(Stream source, int chunkIndex)
        {
            try
            {
                var data = BitConverter.GetBytes(chunkIndex);
                await this.blob.PutBlockAsync(Convert.ToBase64String(data), source).ConfigureAwait(false);
            }
            finally
            {
                source.Dispose();
            }
        }
    }
}
