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
    /// 遵循 Windows NT 文件系统规范，安全映射 Win32 访问掩码与文件生命周期。
    /// </summary>
    public class DokanDiskOperations : IDokanOperations, IDisposable
    {
        private readonly IDiskOperationsService _remote;
        private readonly MetadataCache _cache;
        private readonly string _defaultVolumeLabel;
        private bool _isDisposed;

        /// <summary>
        /// 当监听到远程 Slave 目录文件发生变动时触发
        /// </summary>
        public event EventHandler<FileChangeEventDto>? FileChanged;

        private const DokanNet.FileAccess DataWriteAccess =
            DokanNet.FileAccess.WriteData | DokanNet.FileAccess.AppendData | DokanNet.FileAccess.Delete | DokanNet.FileAccess.GenericWrite | (DokanNet.FileAccess)0x10000000;

        private const DokanNet.FileAccess DataReadAccess =
            DokanNet.FileAccess.ReadData | DokanNet.FileAccess.GenericRead | DokanNet.FileAccess.Execute | DokanNet.FileAccess.GenericExecute | (DokanNet.FileAccess)0x10000000;

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

            // 订阅 Slave 端的目录变动推送事件，实时自动失效本地元数据缓存
            try
            {
                _remote.FileChanged += OnRemoteFileChanged;
            }
            catch
            {
                // 忽略非关键事件挂载异常
            }
        }

        private void OnRemoteFileChanged(object? sender, FileChangeEventDto e)
        {
            if (e == null) return;
            _cache.Invalidate(e.RelativePath);
            if (!string.IsNullOrEmpty(e.OldRelativePath))
            {
                _cache.Invalidate(e.OldRelativePath);
            }

            FileChanged?.Invoke(this, e);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _remote.FileChanged -= OnRemoteFileChanged;
            }
            catch
            {
            }
        }

        private static NtStatus ToNtStatus(int code) => (NtStatus)(uint)code;

        private static System.IO.FileAccess ToSystemFileAccess(DokanNet.FileAccess access, FileMode mode)
        {
            if (mode == FileMode.Append)
            {
                return System.IO.FileAccess.Write;
            }

            bool write = (access & DataWriteAccess) != 0 ||
                         mode == FileMode.Create ||
                         mode == FileMode.CreateNew ||
                         mode == FileMode.Truncate;

            bool read = (access & DataReadAccess) != 0 ||
                        mode == FileMode.Open ||
                        mode == FileMode.OpenOrCreate;

            if (write && read) return System.IO.FileAccess.ReadWrite;
            if (write) return System.IO.FileAccess.Write;
            return System.IO.FileAccess.Read;
        }

        private static FileInformation MapToFileInformation(DiskItemInfoDto item)
        {
            var attrs = item.Attributes;
            if (item.IsDirectory)
            {
                attrs |= FileAttributes.Directory;
            }
            else if (attrs == 0)
            {
                attrs = FileAttributes.Normal;
            }

            return new FileInformation
            {
                FileName = item.Name,
                Attributes = attrs,
                Length = item.Length,
                CreationTime = item.CreationTime ?? DateTime.UtcNow,
                LastAccessTime = item.LastAccessTime ?? DateTime.UtcNow,
                LastWriteTime = item.LastWriteTime ?? DateTime.UtcNow
            };
        }

        private DiskItemInfoDto? QueryItemInfo(string fileName)
        {
            if (_cache.TryGet(fileName, out var cached) && cached != null)
            {
                return cached;
            }

            try
            {
                var item = _remote.GetPathInfo(fileName);
                if (item != null)
                {
                    _cache.Set(fileName, item);
                }
                return item;
            }
            catch
            {
                return null;
            }
        }

        /// <inheritdoc/>
        public NtStatus CreateFile(string fileName, DokanNet.FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            if (fileName == "\\" || fileName == "/")
            {
                info.IsDirectory = true;
                return DokanResult.Success;
            }

            var sysAccess = ToSystemFileAccess(access, mode);
            var sysShare = share | FileShare.ReadWrite | FileShare.Delete;

            var existingItem = QueryItemInfo(fileName);
            bool exists = existingItem != null;
            bool isDir = exists && existingItem!.IsDirectory;

            // 1. 目录或请求目录的处理
            if (info.IsDirectory || isDir)
            {
                info.IsDirectory = true;
                switch (mode)
                {
                    case FileMode.Open:
                        if (!exists) return DokanResult.FileNotFound;
                        if (!isDir) return DokanResult.NotADirectory;
                        return DokanResult.Success;

                    case FileMode.CreateNew:
                        if (exists) return DokanResult.AlreadyExists;
                        try
                        {
                            _remote.CreateDirectory(fileName);
                            _cache.Invalidate(fileName);
                            return DokanResult.Success;
                        }
                        catch
                        {
                            return DokanResult.AccessDenied;
                        }

                    case FileMode.Create:
                    case FileMode.OpenOrCreate:
                        if (!exists)
                        {
                            try
                            {
                                _remote.CreateDirectory(fileName);
                                _cache.Invalidate(fileName);
                                return DokanResult.Success;
                            }
                            catch
                            {
                                return DokanResult.AccessDenied;
                            }
                        }
                        return DokanResult.AlreadyExists;

                    default:
                        return DokanResult.Success;
                }
            }

            // 2. 文件的处理
            switch (mode)
            {
                case FileMode.Open:
                    if (!exists) return DokanResult.FileNotFound;
                    if (isDir)
                    {
                        info.IsDirectory = true;
                        return DokanResult.Success;
                    }

                    var openResult = _remote.OpenFile(fileName, (int)sysAccess, (int)sysShare, (int)FileMode.Open, (int)options, (int)attributes);
                    if (openResult.NtStatusCode != 0) return ToNtStatus(openResult.NtStatusCode);
                    info.Context = openResult.HandleId;
                    return DokanResult.Success;

                case FileMode.CreateNew:
                    if (exists) return DokanResult.AlreadyExists;

                    var createNewResult = _remote.OpenFile(fileName, (int)sysAccess, (int)sysShare, (int)FileMode.CreateNew, (int)options, (int)attributes);
                    if (createNewResult.NtStatusCode != 0) return ToNtStatus(createNewResult.NtStatusCode);
                    info.Context = createNewResult.HandleId;
                    _cache.Invalidate(fileName);
                    return DokanResult.Success;

                case FileMode.Create:
                case FileMode.OpenOrCreate:
                case FileMode.Truncate:
                case FileMode.Append:
                    var openOrCreateResult = _remote.OpenFile(fileName, (int)sysAccess, (int)sysShare, (int)mode, (int)options, (int)attributes);
                    var status = ToNtStatus(openOrCreateResult.NtStatusCode);
                    if (openOrCreateResult.NtStatusCode != 0 && status != DokanResult.AlreadyExists)
                    {
                        return status;
                    }

                    info.Context = openOrCreateResult.HandleId;
                    _cache.Invalidate(fileName);
                    if (mode == FileMode.Truncate)
                    {
                        return DokanResult.Success;
                    }
                    return exists ? DokanResult.AlreadyExists : DokanResult.Success;

                default:
                    return DokanResult.NotImplemented;
            }
        }

        /// <inheritdoc/>
        public void Cleanup(string fileName, IDokanFileInfo info)
        {
            if (info.DeletePending)
            {
                try
                {
                    // 若关联了已打开的文件流，先关闭物理流再删除，避免共享锁定冲突
                    if (info.Context is long handleId && handleId > 0)
                    {
                        _remote.CloseFile(handleId);
                        info.Context = null;
                    }

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
                    // 忽略清理异常
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
                    return DokanResult.Success;
                }
                catch
                {
                    return DokanResult.Unsuccessful;
                }
            }
            return DokanResult.InvalidHandle;
        }

        /// <inheritdoc/>
        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            bytesWritten = 0;
            if (info.Context is long handleId && handleId > 0)
            {
                try
                {
                    long writeOffset = info.WriteToEndOfFile ? -1 : offset;
                    bytesWritten = _remote.WriteBlock(handleId, writeOffset, buffer);
                    _cache.Invalidate(fileName);
                    return DokanResult.Success;
                }
                catch (UnauthorizedAccessException)
                {
                    return DokanResult.AccessDenied;
                }
                catch
                {
                    return DokanResult.Unsuccessful;
                }
            }
            return DokanResult.InvalidHandle;
        }

        /// <inheritdoc/>
        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            if (info.Context is long handleId && handleId > 0)
            {
                try
                {
                    _remote.Flush(handleId);
                    return DokanResult.Success;
                }
                catch
                {
                    return DokanResult.Unsuccessful;
                }
            }
            return DokanResult.Success;
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
                return DokanResult.Success;
            }

            var item = QueryItemInfo(fileName);
            if (item == null)
            {
                fileInfo = default;
                return DokanResult.FileNotFound;
            }

            fileInfo = MapToFileInformation(item);
            return DokanResult.Success;
        }

        /// <inheritdoc/>
        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            try
            {
                var items = _remote.FindFiles(fileName, null);
                _cache.SetBatch(fileName, items);
                files = items.Select(MapToFileInformation).ToList();
                return DokanResult.Success;
            }
            catch
            {
                files = new List<FileInformation>();
                return DokanResult.FileNotFound;
            }
        }

        /// <inheritdoc/>
        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
        {
            try
            {
                var items = _remote.FindFiles(fileName, searchPattern);
                files = items.Select(MapToFileInformation).ToList();
                return DokanResult.Success;
            }
            catch
            {
                files = new List<FileInformation>();
                return DokanResult.FileNotFound;
            }
        }

        /// <inheritdoc/>
        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            try
            {
                _remote.SetFileAttributes(fileName, (int)attributes);
                _cache.Invalidate(fileName);
                return DokanResult.Success;
            }
            catch (UnauthorizedAccessException)
            {
                return DokanResult.AccessDenied;
            }
            catch
            {
                return DokanResult.Unsuccessful;
            }
        }

        /// <inheritdoc/>
        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        {
            try
            {
                _remote.SetFileTime(fileName, creationTime, lastAccessTime, lastWriteTime);
                _cache.Invalidate(fileName);
                return DokanResult.Success;
            }
            catch (UnauthorizedAccessException)
            {
                return DokanResult.AccessDenied;
            }
            catch
            {
                return DokanResult.Unsuccessful;
            }
        }

        /// <inheritdoc/>
        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            // Windows 内核在此检查是否具备删除权限，实际物理删除在 Cleanup 中触发
            var item = QueryItemInfo(fileName);
            if (item == null) return DokanResult.FileNotFound;
            if (item.IsDirectory) return DokanResult.AccessDenied;

            return DokanResult.Success;
        }

        /// <inheritdoc/>
        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            // 检查目录是否存在以及是否为空目录
            var item = QueryItemInfo(fileName);
            if (item == null) return DokanResult.PathNotFound;
            if (!item.IsDirectory) return DokanResult.NotADirectory;

            var children = _remote.FindFiles(fileName, null);
            if (children != null && children.Count > 0)
            {
                return DokanResult.DirectoryNotEmpty;
            }

            return DokanResult.Success;
        }

        /// <inheritdoc/>
        public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            // 若原文件处于打开状态，在重命名前先释放句柄
            if (info.Context is long handleId && handleId > 0)
            {
                try
                {
                    _remote.CloseFile(handleId);
                }
                catch { }
                info.Context = null;
            }

            try
            {
                _remote.MoveFile(oldName, newName, replace);
                _cache.Invalidate(oldName);
                _cache.Invalidate(newName);
                return DokanResult.Success;
            }
            catch (FileNotFoundException)
            {
                return DokanResult.FileNotFound;
            }
            catch (UnauthorizedAccessException)
            {
                return DokanResult.AccessDenied;
            }
            catch (IOException)
            {
                return DokanResult.FileExists;
            }
            catch
            {
                return DokanResult.Unsuccessful;
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
                    return DokanResult.Success;
                }
                catch (UnauthorizedAccessException)
                {
                    return DokanResult.AccessDenied;
                }
                catch
                {
                    return DokanResult.Unsuccessful;
                }
            }
            return DokanResult.InvalidHandle;
        }

        /// <inheritdoc/>
        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            return SetEndOfFile(fileName, length, info);
        }

        /// <inheritdoc/>
        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.Success;
        }

        /// <inheritdoc/>
        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.Success;
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
                return DokanResult.Success;
            }
            catch
            {
                const long defaultSize = 100L * 1024 * 1024 * 1024;
                freeBytesAvailable = defaultSize;
                totalNumberOfBytes = defaultSize;
                totalNumberOfFreeBytes = defaultSize;
                return DokanResult.Success;
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
                return DokanResult.Success;
            }
            catch
            {
                volumeLabel = _defaultVolumeLabel;
                features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.UnicodeOnDisk;
                fileSystemName = "NTFS";
                maximumComponentLength = 256;
                return DokanResult.Success;
            }
        }

        /// <inheritdoc/>
        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
        {
            security = null;
            return DokanResult.NotImplemented;
        }

        /// <inheritdoc/>
        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        /// <inheritdoc/>
        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            return DokanResult.Success;
        }

        /// <inheritdoc/>
        public NtStatus Unmounted(IDokanFileInfo info)
        {
            return DokanResult.Success;
        }

        /// <inheritdoc/>
        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            streams = new List<FileInformation>();
            return DokanResult.NotImplemented;
        }
    }
}
