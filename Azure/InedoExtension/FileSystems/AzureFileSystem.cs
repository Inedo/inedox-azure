using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Inedo.Extensibility.FileSystems;
using Inedo.IO;

namespace Inedo.ProGet.Extensions.Azure.PackageStores;

[DisplayName("Microsoft Azure")]
[Description("A file system backed by Microsoft Azure Blob Storage.")]
[PersistFrom("Inedo.ProGet.Extensions.PackageStores.Azure.AzurePackageStore,ProGetCoreEx")]
[PersistFrom("Inedo.ProGet.Extensions.Azure.PackageStores.AzurePackageStore,Azure")]
[CustomEditor(typeof(AzureFileSystemEditor))]
public sealed partial class AzureFileSystem : FileSystem
{
    private readonly Lazy<BlobContainerClient> blobContainerClient;
    private readonly HashSet<string> virtualDirectories = [];

    public AzureFileSystem()
    {
        this.blobContainerClient = new(() => 
            Uri.TryCreate(this.ContainerUri, UriKind.Absolute, out var containerUrl) 
                ? new(containerUrl, new DefaultAzureCredential()) 
                : new(this.ConnectionString, this.ContainerName)
            );
    }

    [Persistent(Encrypted = true)]
    public string? ConnectionString { get; set; }

    [Persistent]
    public string? ContainerName { get; set; }

    [Persistent]
    public string? TargetPath { get; set; }

    [Persistent]
    public string? ContainerUri { get; set; }

    private string? Prefix => string.IsNullOrEmpty(this.TargetPath) || this.TargetPath.EndsWith('/') ? this.TargetPath : (this.TargetPath + "/");
    private BlobContainerClient Container => this.blobContainerClient.Value;
    private BlockBlobClient CreateBlockBlobClient(string path) => Uri.TryCreate(this.ContainerUri, UriKind.Absolute, out var containerUrl)
        ? new BlobContainerClient(containerUrl, new DefaultAzureCredential()).GetBlockBlobClient(path)
        : new(this.ConnectionString, path, this.ContainerName);

    public override async Task<Stream?> OpenReadAsync(string fileName, FileAccessHints hints = FileAccessHints.Default, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentNullException(nameof(fileName));

        var path = this.BuildPath(fileName);
        var blobClient = this.Container.GetBlobClient(path);
        if (!await blobClient.ExistsAsync(cancellationToken))
            throw new FileNotFoundException();

        return await blobClient.OpenReadAsync(null, cancellationToken).ConfigureAwait(false);
    }
    public override Task<Stream> CreateFileAsync(string fileName, FileAccessHints hints = FileAccessHints.Default, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentNullException(nameof(fileName));

        var path = this.BuildPath(fileName);
        var blobClient = this.Container.GetBlobClient(path);

        return blobClient.OpenWriteAsync(true, options: new(), cancellationToken: cancellationToken);
    }

    public override async Task CopyFileAsync(string sourceName, string targetName, bool overwrite, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sourceName))
            throw new ArgumentNullException(nameof(sourceName));
        if (string.IsNullOrEmpty(targetName))
            throw new ArgumentNullException(nameof(targetName));

        var source = this.Container.GetBlobClient(this.BuildPath(sourceName));
        var target = this.Container.GetBlobClient(this.BuildPath(targetName));

        if (!await source.ExistsAsync(cancellationToken).ConfigureAwait(false))
            throw new FileNotFoundException($"{sourceName} not found.");

        if (!overwrite && await target.ExistsAsync(cancellationToken).ConfigureAwait(false))
            throw new IOException($"{targetName} exists, but overwrite is not allowed.");

        await target.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var status = await target.StartCopyFromUriAsync(source.Uri, null, cancellationToken).ConfigureAwait(false);
        await status.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
    }
    public override async Task DeleteFileAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = this.BuildPath(fileName);
        var blobClient = this.Container.GetBlobClient(path);
        await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    public override Task CreateDirectoryAsync(string directoryName, CancellationToken cancellationToken = default)
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

        return Task.CompletedTask;
    }

    public override async ValueTask<bool> FileExistsAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;

        try
        {
            var path = this.BuildPath(fileName);
            var blobClient = this.Container.GetBlobClient(path);
            return await blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }
    public override async ValueTask<bool> DirectoryExistsAsync(string directoryName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(directoryName))
            return true;

        var path = this.BuildPath(directoryName);

        if (string.IsNullOrEmpty(path))
            return true;
        if (this.virtualDirectories.Contains(path))
            return true;

        var parentPath = PathEx.GetDirectoryName(directoryName);
        var childName = PathEx.GetFileName(path);

        await foreach (var item in this.ListContentsAsync(parentPath ?? string.Empty, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsDirectory && item.Name == childName)
                return true;
        }

        return false;
    }

    public override async Task DeleteDirectoryAsync(string directoryName, bool recursive, CancellationToken cancellationToken = default)
    {
        if (!recursive)
            return;

        var path = this.BuildPath(directoryName);
        if (!string.IsNullOrEmpty(path))
            path += "/";

        var files = new List<string>();

        await foreach (var b in this.Container.GetBlobsAsync(prefix: path, cancellationToken: cancellationToken).ConfigureAwait(false))
            files.Add(b.Name);

        foreach (var file in files)
        {
            var blobClient = this.Container.GetBlobClient(file);
            await blobClient.DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
    public override async IAsyncEnumerable<FileSystemItem> ListContentsAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var path2 = this.BuildPath(path);

        if (!string.IsNullOrEmpty(path2) && !path2.EndsWith('/'))
            path2 += "/";

        var dirs = new HashSet<string>();

        await foreach (var item in this.Container.GetBlobsByHierarchyAsync(delimiter: "/", prefix: path2, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (item.IsPrefix)
            {
                var name = item.Prefix.AsSpan(path2.Length).TrimEnd('/').ToString();
                yield return new AzureFileSystemItem(name);
                dirs.Add(name);
            }
            else
            {
                yield return new AzureFileSystemItem(PathEx.GetFileName(item.Blob.Name)!, item.Blob.Properties.ContentLength.GetValueOrDefault(), item.Blob.Properties.LastModified);
            }
        }

        foreach (var d in this.GetVirtualDirectories(path2))
        {
            if (!dirs.Contains(d))
                yield return new AzureFileSystemItem(d);
        }
    }
    public override async Task<FileSystemItem?> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        if (await this.DirectoryExistsAsync(path, cancellationToken).ConfigureAwait(false))
            return new AzureFileSystemItem(PathEx.GetFileName(path) ?? string.Empty);

        var path2 = this.BuildPath(path);
        var blobClient = this.Container.GetBlobClient(path2);
        if (!await blobClient.ExistsAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var props = await blobClient.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return new AzureFileSystemItem(path2, props.Value.ContentLength, props.Value.LastModified);
    }
    public override async ValueTask<long?> GetDirectoryContentSizeAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        var path2 = this.BuildPath(path);
        if (!string.IsNullOrEmpty(path2))
            path2 += "/";

        long size = 0;

        if (recursive)
        {
            await foreach (var b in this.Container.GetBlobsAsync(prefix: path2, cancellationToken: cancellationToken).ConfigureAwait(false))
                size += b.Properties.ContentLength.GetValueOrDefault();
        }
        else
        {
            await foreach (var b in this.Container.GetBlobsByHierarchyAsync(delimiter: "/", prefix: path2, cancellationToken: cancellationToken).ConfigureAwait(false))
                size += b.Blob?.Properties.ContentLength ?? 0;
        }

        return size;
    }

    public override Task<UploadStream> BeginResumableUploadAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentNullException(nameof(fileName));

        var path = this.BuildPath(fileName);
        var client = this.CreateBlockBlobClient(path);
        return Task.FromResult<UploadStream>(new BlobUploadStream(client));
    }
    public override Task<UploadStream> ContinueResumableUploadAsync(string fileName, byte[] state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentNullException(nameof(fileName));

        int blockCount = DecodeBlockIndex(state);

        var path = this.BuildPath(fileName);
        var client = this.CreateBlockBlobClient(path);
        return Task.FromResult<UploadStream>(new BlobUploadStream(client, blockCount));
    }
    public override async Task CompleteResumableUploadAsync(string fileName, byte[] state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentNullException(nameof(fileName));

        int blockCount = DecodeBlockIndex(state);

        var path = this.BuildPath(fileName);
        var client = this.CreateBlockBlobClient(path);
        if (blockCount == 0)
        {
            using var s = await client.OpenWriteAsync(true, options: new(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await client.CommitBlockListAsync(Enumerable.Range(0, blockCount).Select(EncodeBlockIndex), null, cancellationToken).ConfigureAwait(false);
        }
    }
    public override Task CancelResumableUploadAsync(string fileName, byte[] state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentNullException(nameof(fileName));

        var path = this.BuildPath(fileName);
        var client = this.CreateBlockBlobClient(path);
        return client.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public override RichDescription GetDescription()
    {
        if (string.IsNullOrEmpty(this.ContainerName))
            return new RichDescription(
            "Azure (using Container URI)"
        );

        return new RichDescription(
            "Azure (using the ",
            new Hilite(this.ContainerName),
            " blob container)"
        );
    }

    private static string EncodeBlockIndex(int index)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, index);
        return Convert.ToBase64String(buffer);
    }
    private static int DecodeBlockIndex(byte[]? data)
    {
        if (data == null || data.Length < 4)
            return 0;

        return BinaryPrimitives.ReadInt32LittleEndian(data);
    }
    private IEnumerable<string> GetVirtualDirectories(string path)
    {
        return getNames().Distinct();

        IEnumerable<string> getNames()
        {
            if (string.IsNullOrEmpty(path))
            {
                foreach (var p in this.virtualDirectories)
                    yield return p.Split('/', 2)[0];
            }
            else
            {
                var path2 = path;
                if (!path2.EndsWith('/'))
                    path2 += "/";

                foreach (var p in this.virtualDirectories)
                {
                    if (p.StartsWith(path2))
                    {
                        var remainder = p[path2.Length..];
                        if (!string.IsNullOrEmpty(remainder))
                            yield return remainder.Split('/', 2, StringSplitOptions.None)[0];
                    }
                }
            }
        }
    }
    private string BuildPath(string path)
    {
        return MultiSlashRegex().Replace(string.Concat(this.Prefix, path.AsSpan().Trim('/')), string.Empty);
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

    [GeneratedRegex(@"/{2,}")]
    private static partial Regex MultiSlashRegex();
}
