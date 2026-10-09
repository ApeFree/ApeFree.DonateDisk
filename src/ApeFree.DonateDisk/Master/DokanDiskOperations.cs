using ApeFree.DonateDisk.Contracts;
using DokanNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;

namespace ApeFree.DonateDisk.Master
{
    /// <summary>
    /// 将 Windows 内核文件系统 I/O 请求桥接至远程 Slave 端 RPC 接口的 DokanOperations 实现。
    /// </summary>
    public class DokanDiskOperations : IDokanOperations
    {
        private readonly IDiskOperationsService _remote;
        private readonly MetadataCache _cache;
        private readonly string _defaultVolumeLabel;

        /// <summary>
        /// 构造 DokanDiskOperations 桥接器
        /// </summary>
        /// <param name="remote">远程磁盘 RPC 代理对象</param>
        /// <param name="cache">元数据缓存（可选）</param>
        /// <param name="defaultVolumeLabel">默认卷标名</param>
        public DokanDiskOperations(IDiskOperationsService remote, MetadataCache? cache = null, string? defaultVolumeLabel = null)
        {
            _remote = remote ?? throw new ArgumentNullException(nameof(remote));
            _cache = cache ?? new MetadataCache();
            _defaultVolumeLabel = defaultVolumeLabel ?? "DonateDisk";
        }

        private static FileInformation MapToFileInformation(DiskItemInfoDto item)
        {
            return new FileInformation
            {
                FileName = item.Name,
                Attributes = item.Attributes,
                Length = item.Length,
                CreationTime = item.CreationTime ?? DateTime.UtcNow,
                LastAccessTime = item.LastAccessTime ?? DateTime.UtcNow,
                LastWriteTime = item.LastWriteTime ?? DateTime.UtcNow
            };
        }

        /// <inheritdoc/>
        public NtStatus CreateFile(string fileName, DokanNet.FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            if (fileName == "\\" || fileName == "/")
            {
                info.IsDirectory = true;
                return NtStatus.Success;
            }

            // 若用户仅是打开目录
            if (info.IsDirectory)
            {
                return NtStatus.Success;
            }

            try
            {
                // 先探测路径是否为已有目录
                if (_cache.TryGet(fileName, out var cached) && cached != null && cached.IsDirectory)
                {
                    info.IsDirectory = true;
                    return NtStatus.Success;
                }

                var openResult = _remote.OpenFile(fileName, (int)access, (int)share, (int)mode, (int)options, (int)attributes);
                if (openResult.NtStatusCode == 0)
                {
                    info.IsDirectory = openResult.IsDirectory;
                    if (!openResult.IsDirectory && openResult.HandleId > 0)
                    {
                        info.Context = openResult.HandleId;
                    }
                    return NtStatus.Success;
                }

                if (openResult.IsDirectory)
                {
                    info.IsDirectory = true;
                    return NtStatus.Success;
                }

                return (NtStatus)openResult.NtStatusCode;
            }
            catch (Exception)
            {
                return NtStatus.NoSuchFile;
            }
        }

        /// <inheritdoc/>
        public void Cleanup(string fileName, IDokanFileInfo info)
        {
            if (info.DeletePending)
            {
                try
                {
                    if (info.IsDirectory)
                    {
                        _remote.DeleteDirectory(fileName);
                    }
                    else
                    {
                        _remote.DeleteFile(fileName);
                    }
                    _cache.Invalidate(fileName);
                }
                catch
                {
                    // 忽略清理时的异常
                }
            }
        }

        /// <inheritdoc/>
        public void CloseFile(string fileName, IDokanFileInfo info)
        {
            if (info.Context is long handleId && handleId > 0)
            {
                try
                {
                    _remote.CloseFile(handleId);
                }
                catch
                {
                    // 忽略关闭句柄异常
                }
                info.Context = null;
            }
        }

        /// <inheritdoc/>
        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
        {
            bytesRead = 0;
            if (info.Context is long handleId && handleId > 0)
            {
                try
                {
                    var data = _remote.ReadBlock(handleId, offset, buffer.Length);
                    if (data != null && data.Length > 0)
                    {
                        Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
                        bytesRead = data.Length;
                    }
                    return NtStatus.Success;
                }
                catch
                {
                    return NtStatus.Unsuccessful;
                }
            }
            return NtStatus.AccessDenied;
        }

        /// <inheritdoc/>
        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            bytesWritten = 0;
            if (info.Context is long handleId && handleId > 0)
            {
                try
                {
                    bytesWritten = _remote.WriteBlock(handleId, offset, buffer);
                    _cache.Invalidate(fileName);
                    return NtStatus.Success;
                }
                catch
                {
                    return NtStatus.AccessDenied;
                }
            }
            return NtStatus.AccessDenied;
        }

        /// <inheritdoc/>
        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            if (info.Context is long handleId && handleId > 0)
            {
                try
                {
                    _remote.Flush(handleId);
                    return NtStatus.Success;
                }
                catch
                {
                    return NtStatus.Unsuccessful;
                }
            }
            return NtStatus.Success;
        }

        /// <inheritdoc/>
        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            if (fileName == "\\" || fileName == "/")
            {
                fileInfo = new FileInformation
                {
                    FileName = "\\",
                    Attributes = FileAttributes.Directory,
                    CreationTime = DateTime.UtcNow,
                    LastAccessTime = DateTime.UtcNow,
                    LastWriteTime = DateTime.UtcNow,
                    Length = 0
                };
                return NtStatus.Success;
            }

            if (_cache.TryGet(fileName, out var cached) && cached != null)
            {
                fileInfo = MapToFileInformation(cached);
                return NtStatus.Success;
            }

            try
            {
                var item = _remote.GetPathInfo(fileName);
                if (item == null)
                {
                    fileInfo = default;
                    return NtStatus.NoSuchFile;
                }

                _cache.Set(fileName, item);
                fileInfo = MapToFileInformation(item);
                return NtStatus.Success;
            }
            catch
            {
                fileInfo = default;
                return NtStatus.NoSuchFile;
            }
        }

        /// <inheritdoc/>
        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            try
            {
                var items = _remote.FindFiles(fileName, null);
                _cache.SetBatch(fileName, items);
                files = items.Select(MapToFileInformation).ToList();
                return NtStatus.Success;
            }
            catch
            {
                files = new List<FileInformation>();
                return NtStatus.NoSuchFile;
            }
        }

        /// <inheritdoc/>
        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
        {
            try
            {
                var items = _remote.FindFiles(fileName, searchPattern);
                files = items.Select(MapToFileInformation).ToList();
                return NtStatus.Success;
            }
            catch
            {
                files = new List<FileInformation>();
                return NtStatus.NoSuchFile;
            }
        }

        /// <inheritdoc/>
        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            try
            {
                _remote.SetFileAttributes(fileName, (int)attributes);
                _cache.Invalidate(fileName);
                return NtStatus.Success;
            }
            catch
            {
                return NtStatus.AccessDenied;
            }
        }

        /// <inheritdoc/>
        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        {
            try
            {
                _remote.SetFileTime(fileName, creationTime, lastAccessTime, lastWriteTime);
                _cache.Invalidate(fileName);
                return NtStatus.Success;
            }
            catch
            {
                return NtStatus.AccessDenied;
            }
        }

        /// <inheritdoc/>
        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            _cache.Invalidate(fileName);
            return NtStatus.Success;
        }

        /// <inheritdoc/>
        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            _cache.Invalidate(fileName);
            return NtStatus.Success;
        }

        /// <inheritdoc/>
        public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            try
            {
                _remote.MoveFile(oldName, newName, replace);
                _cache.Invalidate(oldName);
                _cache.Invalidate(newName);
                return NtStatus.Success;
            }
            catch
            {
                return NtStatus.AccessDenied;
            }
        }

        /// <inheritdoc/>
        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            if (info.Context is long handleId && handleId > 0)
            {
                try
                {
                    _remote.SetEndOfFile(handleId, length);
                    _cache.Invalidate(fileName);
                    return NtStatus.Success;
                }
                catch
                {
                    return NtStatus.AccessDenied;
                }
            }
            return NtStatus.AccessDenied;
        }

        /// <inheritdoc/>
        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            return SetEndOfFile(fileName, length, info);
        }

        /// <inheritdoc/>
        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return NtStatus.Success;
        }

        /// <inheritdoc/>
        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return NtStatus.Success;
        }

        /// <inheritdoc/>
        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
        {
            try
            {
                var space = _remote.GetDiskFreeSpace();
                freeBytesAvailable = space.FreeBytesAvailable;
                totalNumberOfBytes = space.TotalNumberOfBytes;
                totalNumberOfFreeBytes = space.TotalNumberOfFreeBytes;
                return NtStatus.Success;
            }
            catch
            {
                const long defaultSize = 100L * 1024 * 1024 * 1024;
                freeBytesAvailable = defaultSize;
                totalNumberOfBytes = defaultSize;
                totalNumberOfFreeBytes = defaultSize;
                return NtStatus.Success;
            }
        }

        /// <inheritdoc/>
        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
        {
            try
            {
                var vol = _remote.GetVolumeInfo();
                volumeLabel = vol.VolumeLabel ?? _defaultVolumeLabel;
                features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;
                fileSystemName = vol.FileSystemName ?? "NTFS";
                maximumComponentLength = vol.MaximumComponentLength;
                return NtStatus.Success;
            }
            catch
            {
                volumeLabel = _defaultVolumeLabel;
                features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;
                fileSystemName = "NTFS";
                maximumComponentLength = 256;
                return NtStatus.Success;
            }
        }

        /// <inheritdoc/>
        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
        {
            security = null;
            return NtStatus.NotImplemented;
        }

        /// <inheritdoc/>
        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            return NtStatus.NotImplemented;
        }

        /// <inheritdoc/>
        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            return NtStatus.Success;
        }

        /// <inheritdoc/>
        public NtStatus Unmounted(IDokanFileInfo info)
        {
            return NtStatus.Success;
        }

        /// <inheritdoc/>
        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            streams = new List<FileInformation>();
            return NtStatus.NotImplemented;
        }
    }
}
