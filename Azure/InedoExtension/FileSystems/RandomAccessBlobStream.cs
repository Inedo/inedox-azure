using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Storage.Blob;

namespace Inedo.ProGet.Extensions.Azure.PackageStores
{
    internal sealed class RandomAccessBlobStream : Stream
    {
        private readonly ICloudBlob blob;

        public RandomAccessBlobStream(ICloudBlob blob)
        {
            this.blob = blob;
            this.Length = blob.Properties.Length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            int bytesToRead = (int)Math.Min(count, this.Length - this.Position);
            if (bytesToRead <= 0)
                return 0;

            return this.blob.DownloadRangeToByteArray(buffer, offset, this.Position, bytesToRead);
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            int bytesToRead = (int)Math.Min(count, this.Length - this.Position);
            if (bytesToRead <= 0)
                return 0;

            return await this.blob.DownloadRangeToByteArrayAsync(buffer, offset, this.Position, bytesToRead, cancellationToken).ConfigureAwait(false);
        }
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
        public override void Flush()
        {
        }
        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
