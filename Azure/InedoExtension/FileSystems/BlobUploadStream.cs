using Azure.Storage.Blobs.Specialized;
using Inedo.Extensibility.FileSystems;
using Inedo.IO;

namespace Inedo.ProGet.Extensions.Azure.PackageStores;

internal sealed class BlobUploadStream(BlockBlobClient client, int chunkIndex = 0) : UploadStream
{
    private const long MaxChunkSize = 50 * 1024 * 1024;
    private readonly BlockBlobClient client = client;
    private int chunkIndex = chunkIndex;
    private readonly object syncLock = new();
    private TemporaryStream writeStream = new();
    private Task? uploadTask;
    private bool disposed;

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
            throw new ArgumentException("The sum of offset and count exceeded the bounds of the array.");

        this.Write(buffer.AsSpan(offset, count));
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset + count > buffer.Length)
            throw new ArgumentException("The sum of offset and count exceeded the bounds of the array.");

        return this.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
    public override void WriteByte(byte value)
    {
        this.EnsureNotDisposed();
        this.writeStream.WriteByte(value);
        this.IncrementBytesWritten(1);
        this.CheckAndBeginBackgroundUpload();
    }

    public override async Task<byte[]?> CommitAsync(CancellationToken cancellationToken)
    {
        this.EnsureNotDisposed();
        await this.CompleteUploadAsync().ConfigureAwait(false);
        return BitConverter.GetBytes(this.chunkIndex);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        this.EnsureNotDisposed();
        if (buffer.IsEmpty)
            return;

        this.writeStream.Write(buffer);
        this.IncrementBytesWritten(buffer.Length);
        this.CheckAndBeginBackgroundUpload();
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        this.EnsureNotDisposed();
        if (buffer.IsEmpty)
            return;

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
        Task? waitTask;
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
            await this.client.StageBlockAsync(Convert.ToBase64String(data), this.writeStream).ConfigureAwait(false);
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
            await this.client.StageBlockAsync(Convert.ToBase64String(data), source).ConfigureAwait(false);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }
    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(this.disposed, this);
}
