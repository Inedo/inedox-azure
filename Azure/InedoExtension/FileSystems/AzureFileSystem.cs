using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inedo.Documentation;
using Inedo.Extensibility.FileSystems;
using Inedo.IO;
using Inedo.Serialization;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;

namespace Inedo.ProGet.Extensions.Azure.PackageStores
{
    [DisplayName("Microsoft Azure")]
    [Description("A file system backed by Microsoft Azure Blob Storage.")]
    [PersistFrom("Inedo.ProGet.Extensions.PackageStores.Azure.AzurePackageStore,ProGetCoreEx")]
    [PersistFrom("Inedo.ProGet.Extensions.Azure.PackageStores.AzurePackageStore,Azure")]
    public sealed class AzureFileSystem : FileSystem
    {
        private static readonly LazyRegex MultiSlashPattern = new(@"/{2,}");
        private readonly Lazy<CloudStorageAccount> cloudStorageAccount;
        private readonly Lazy<CloudBlobClient> cloudBlobClient;
        private readonly Lazy<CloudBlobContainer> cloudBlobContainer;
        private readonly HashSet<string> virtualDirectories = new();

        public AzureFileSystem()
        {
            this.cloudStorageAccount = new(() => CloudStorageAccount.Parse(this.ConnectionString));
            this.cloudBlobClient = new(() => this.Account.CreateCloudBlobClient());
            this.cloudBlobContainer = new(() => this.Client.GetContainerReference(this.ContainerName));
        }

        [Required]
        [Persistent(Encrypted = true)]
        [DisplayName("Connection string")]
        [Description("A Microsoft Azure connection string, like <code>DefaultEndpointsProtocol=https;AccountName=account-name;AccountKey=account-key</code>")]
        public string ConnectionString { get; set; }

        [Required]
        [Persistent]
        [DisplayName("Container")]
        [Description("The name of the Azure Blob Container that will receive the uploaded files.")]
        public string ContainerName { get; set; }

        [Persistent]
        [DisplayName("Target path")]
        [Description("The path in the specified Azure Blob Container that will received the uploaded files; the default is the root.")]
        public string TargetPath { get; set; }

        private CloudStorageAccount Account => this.cloudStorageAccount.Value;
        private CloudBlobClient Client => this.cloudBlobClient.Value;
        private CloudBlobContainer Container => this.cloudBlobContainer.Value;
        private string Prefix => string.IsNullOrEmpty(this.TargetPath) || this.TargetPath.EndsWith("/") ? this.TargetPath : (this.TargetPath + "/");

        public override async Task<Stream> OpenReadAsync(string fileName, FileAccessHints hints = FileAccessHints.Default, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentNullException(nameof(fileName));

            var path = this.BuildPath(fileName);
            if (!await this.Container.ExistsAsync(cancellationToken).ConfigureAwait(false))
                throw new FileNotFoundException();

            var file = await this.Container.GetBlobReferenceFromServerAsync(path, cancellationToken).ConfigureAwait(false);
            await file.FetchAttributesAsync(cancellationToken).ConfigureAwait(false);

            if (hints.HasFlag(FileAccessHints.RandomAccess))
                return new BufferedStream(new RandomAccessBlobStream(file), 32 * 1024);
            else
                return new PositionStream(await file.OpenReadAsync(cancellationToken).ConfigureAwait(false), file.Properties.Length);
        }
        public override async Task<Stream> CreateFileAsync(string fileName, FileAccessHints hints = FileAccessHints.Default, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentNullException(nameof(fileName));

            var path = this.BuildPath(fileName);
            var blob = this.Container.GetBlockBlobReference(path);

            var stream = await blob.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return new AzureWriteStream(stream);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }

        public override async Task CopyFileAsync(string sourceName, string targetName, bool overwrite)
        {
            var source = this.Container.GetBlobReference(this.BuildPath(sourceName));
            var target = this.Container.GetBlobReference(this.BuildPath(targetName));

            if (!await BlobExistsAsync(source).ConfigureAwait(false))
                throw new FileNotFoundException($"{sourceName} not found.");

            if (!overwrite && await BlobExistsAsync(target).ConfigureAwait(false))
                throw new IOException($"{targetName} exists, but overwrite is not allowed.");

            _ = await source.AcquireLeaseAsync(null).ConfigureAwait(false);
            try
            {
                await target.StartCopyAsync(source.Uri).ConfigureAwait(false);
                while (target.CopyState?.Status == CopyStatus.Pending)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    await target.FetchAttributesAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (source != null)
                {
                    await source.FetchAttributesAsync().ConfigureAwait(false);
                    if (source.Properties.LeaseState != LeaseState.Available)
                        await source.BreakLeaseAsync(TimeSpan.Zero).ConfigureAwait(false);
                }
            }
        }
        public override async Task DeleteFileAsync(string fileName)
        {
            var blob = await this.Container.GetBlobReferenceFromServerAsync(this.BuildPath(fileName)).ConfigureAwait(false);
            await blob.DeleteIfExistsAsync().ConfigureAwait(false);
        }
        public override Task CreateDirectoryAsync(string directoryName)
        {
            if (!string.IsNullOrEmpty(directoryName))
            {
                var path = this.BuildPath(directoryName);
                if (!string.IsNullOrEmpty(path))
                {
                    if (this.virtualDirectories.Add(path))
                    {
                        var parts = path.Split('/');
                        for (int i = 1; i < parts.Length; i++)
                            this.virtualDirectories.Add(string.Join("/", parts.Take(i)));
                    }
                }
            }

            return InedoLib.CompletedTask;
        }

        public override async Task<bool> FileExistsAsync(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;

            try
            {
                var path = this.BuildPath(fileName);
                var blob = await this.Container.GetBlobReferenceFromServerAsync(path).ConfigureAwait(false);
                return await blob.ExistsAsync().ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }
        public override Task<bool> DirectoryExistsAsync(string directoryName)
        {
            return Task.FromResult(dirExists());

            bool dirExists()
            {
                if (string.IsNullOrEmpty(directoryName))
                    return true;

                var path = this.BuildPath(directoryName);

                if (string.IsNullOrEmpty(path))
                    return true;
                if (this.virtualDirectories.Contains(path))
                    return true;

                var parentPath = PathEx.GetDirectoryName(path);
                var childName = PathEx.GetFileName(path);

                var parentDir = this.Container.GetDirectoryReference(parentPath);
                return parentDir
                    .ListBlobs()
                    .OfType<CloudBlobDirectory>()
                    .Any(d => PathEx.GetFileName(d.Prefix) == childName);
            }
        }

        public async override Task DeleteDirectoryAsync(string directoryName, bool recursive)
        {
            if (!recursive)
                return;

            var directory = this.Container.GetDirectoryReference(this.BuildPath(directoryName));
            var files = directory.ListBlobs(true);
            foreach (var file in files)
            {
                if (file is CloudBlob blob)
                    await blob.DeleteAsync().ConfigureAwait(false);
            }
        }
        public override Task<IEnumerable<FileSystemItem>> ListContentsAsync(string path)
        {
            var path2 = this.BuildPath(path);
            var directory = this.Container.GetDirectoryReference(path2);
            var files = directory.ListBlobs();
            var contents = new List<FileSystemItem>();
            foreach (var file in files)
            {
                if (file is CloudBlobDirectory dir)
                    contents.Add(new AzureFileSystemItem(PathEx.GetFileName(dir.Prefix)));

                if (file is ICloudBlob blob)
                    contents.Add(new AzureFileSystemItem(PathEx.GetFileName(blob.Name), blob.Properties.Length, blob.Properties.LastModified));
            }

            foreach (var d in this.GetVirtualDirectories(path2))
            {
                if (!contents.Any(i => i.Name == d))
                    contents.Add(new AzureFileSystemItem(d));
            }

            return Task.FromResult((IEnumerable<FileSystemItem>)contents);
        }
        public async override Task<FileSystemItem> GetInfoAsync(string path)
        {
            var path2 = this.BuildPath(path);

            if (await this.DirectoryExistsAsync(path2).ConfigureAwait(false))
                return new AzureFileSystemItem(PathEx.GetFileName(path2));

            try
            {
                var file = await this.Container.GetBlobReferenceFromServerAsync(path2).ConfigureAwait(false);
                await file.FetchAttributesAsync().ConfigureAwait(false);
                if (!await file.ExistsAsync().ConfigureAwait(false))
                {
                    var directory = this.Container.GetDirectoryReference(path2);
                    var contents = await directory.ListBlobsSegmentedAsync(true, BlobListingDetails.None, 1, null, null, null).ConfigureAwait(false);
                    if (contents.Results.Any())
                        return new AzureFileSystemItem(PathEx.GetFileName(path2));

                    return null;
                }

                return new AzureFileSystemItem(PathEx.GetFileName(path2), file.Properties.Length, file.Properties.LastModified);
            }
            catch
            {
                return null;
            }
        }
        public override Task<long?> GetDirectoryContentSizeAsync(string path, bool recursive, CancellationToken cancellationToken = default)
        {
            var path2 = this.BuildPath(path);
            var directory = this.Container.GetDirectoryReference(path2);

            return Task.FromResult<long?>(
                directory.ListBlobs(true)
                    .OfType<ICloudBlob>()
                    .Sum(b => b.Properties.Length)
            );
        }

        public override Task<UploadStream> BeginResumableUploadAsync(string fileName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentNullException(nameof(fileName));

            var path = this.BuildPath(fileName);
            var blob = this.Container.GetBlockBlobReference(path);
            return Task.FromResult<UploadStream>(new BlobUploadStream(blob));
        }
        public override Task<UploadStream> ContinueResumableUploadAsync(string fileName, byte[] state, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentNullException(nameof(fileName));

            int blockCount = 0;
            if (state != null && state.Length >= 4)
                blockCount = BitConverter.ToInt32(state, 0);

            var path = this.BuildPath(fileName);
            var blob = this.Container.GetBlockBlobReference(path);
            return Task.FromResult<UploadStream>(new BlobUploadStream(blob, blockCount));
        }
        public override async Task CompleteResumableUploadAsync(string fileName, byte[] state, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentNullException(nameof(fileName));

            int blockCount = 0;
            if (state != null && state.Length >= 4)
                blockCount = BitConverter.ToInt32(state, 0);

            var path = this.BuildPath(fileName);
            var blob = this.Container.GetBlockBlobReference(path);
            if (blockCount == 0)
            {
                using var s = await blob.OpenWriteAsync(cancellationToken).ConfigureAwait(false);
                await s.CommitAsync().ConfigureAwait(false);
            }
            else
            {
                await blob.PutBlockListAsync(Enumerable.Range(0, blockCount).Select(i => Convert.ToBase64String(BitConverter.GetBytes(i))), cancellationToken).ConfigureAwait(false);
            }
        }
        public override Task CancelResumableUploadAsync(string fileName, byte[] state, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(fileName))
                throw new ArgumentNullException(nameof(fileName));

            var path = this.BuildPath(fileName);
            var blob = this.Container.GetBlockBlobReference(path);
            return blob.DeleteIfExistsAsync(cancellationToken);
        }

        public override RichDescription GetDescription()
        {
            if (string.IsNullOrEmpty(this.ContainerName))
                return base.GetDescription();

            return new RichDescription(
                "Azure (using the ",
                new Hilite(this.ContainerName),
                " blob container)"
            );
        }

        private IEnumerable<string> GetVirtualDirectories(string path)
        {
            return getNames().Distinct();

            IEnumerable<string> getNames()
            {
                if (string.IsNullOrEmpty(path))
                {
                    foreach (var p in this.virtualDirectories)
                        yield return p.Split(new[] { '/' }, 2, StringSplitOptions.None)[0];
                }
                else
                {
                    var path2 = path;
                    if (!path2.EndsWith("/"))
                        path2 += "/";

                    foreach (var p in this.virtualDirectories)
                    {
                        if (p.StartsWith(path2))
                        {
                            var remainder = p.Substring(path2.Length);
                            if (!string.IsNullOrEmpty(remainder))
                                yield return remainder.Split(new[] { '/' }, 2, StringSplitOptions.None)[0];
                        }
                    }
                }
            }
        }
        private string BuildPath(string path)
        {
            // Collapse slashes.
            path = MultiSlashPattern.Replace(path.Trim('/'), "");

            return this.Prefix + path;
        }
        private static async Task<bool> BlobExistsAsync(CloudBlob blob)
        {
            if (blob == null)
                return false;

            try
            {
                await blob.FetchAttributesAsync().ConfigureAwait(false);
            }
            catch
            {
                return false;
            }

            return await blob.ExistsAsync().ConfigureAwait(false);
        }

        private sealed class AzureFileSystemItem : FileSystemItem
        {
            public AzureFileSystemItem(string name)
            {
                this.Name = name;
                this.Size = null;
                this.IsDirectory = true;
            }
            public AzureFileSystemItem(string name, long size, DateTimeOffset? lastModifyTime)
            {
                this.Name = name;
                this.Size = size;
                this.IsDirectory = false;
                this.LastModifyTime = lastModifyTime;
            }

            public override string Name { get; }
            public override long? Size { get; }
            public override bool IsDirectory { get; }
            public override DateTimeOffset? LastModifyTime { get; }
        }

        private sealed class AzureWriteStream : Stream
        {
            private readonly CloudBlobStream inner;
            private bool disposed;

            public AzureWriteStream(CloudBlobStream inner) => this.inner = inner;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => this.inner.Length;
            public override long Position
            {
                get => this.inner.Position;
                set => this.inner.Position = value;
            }

            public override void Flush() => this.inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => this.inner.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => this.inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => this.inner.Seek(offset, origin);
            public override void SetLength(long value) => this.inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => this.inner.Write(buffer, offset, count);
            public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => this.inner.CopyToAsync(destination, bufferSize, cancellationToken);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.inner.ReadAsync(buffer, offset, count, cancellationToken);
            public override int ReadByte() => this.inner.ReadByte();
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.inner.WriteAsync(buffer, offset, count, cancellationToken);
            public override void WriteByte(byte value) => this.inner.WriteByte(value);

#if !NET452
            public override int Read(Span<byte> buffer) => this.inner.Read(buffer);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => this.inner.ReadAsync(buffer, cancellationToken);
            public override void Write(ReadOnlySpan<byte> buffer) => this.inner.Write(buffer);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => this.inner.WriteAsync(buffer, cancellationToken);
            public override void CopyTo(Stream destination, int bufferSize) => this.inner.CopyTo(destination, bufferSize);
            public override async ValueTask DisposeAsync()
            {
                if (!this.disposed)
                {
                    await this.inner.CommitAsync().ConfigureAwait(false);
                    await this.inner.DisposeAsync().ConfigureAwait(false);
                    this.disposed = true;
                }

                await base.DisposeAsync().ConfigureAwait(false);
            }
#endif

            protected override void Dispose(bool disposing)
            {
                if (!this.disposed)
                {
                    if (disposing)
                    {
                        this.inner.Commit();
                        this.inner.Dispose();
                    }

                    this.disposed = true;
                }

                base.Dispose(disposing);
            }
        }
    }
}
