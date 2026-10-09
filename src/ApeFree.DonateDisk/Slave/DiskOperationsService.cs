using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Contracts;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ApeFree.DonateDisk.Slave
{
    /// <summary>
    /// 运行在 Slave 端的虚拟磁盘操作核心实现。
    /// 负责映射真实物理磁盘目录，提供强安全沙箱的文件系统操作，并通过 RPC 暴露给 Master 端。
    /// 内置 FileSystemWatcher 实时监听目录变动并通过 RPC 事件主动通知 Master。
    /// </summary>
    public class DiskOperationsService : IDiskOperationsService, IDisposable
    {
        private readonly SafePathResolver _pathResolver;
        private readonly ConcurrentDictionary<long, FileStreamSession> _sessions = new ConcurrentDictionary<long, FileStreamSession>();
        private readonly FileSystemWatcher _watcher;
        private long _handleCounter = 100;
        private bool _isDisposed;

        /// <inheritdoc/>
        public event EventHandler<FileChangeEventDto>? FileChanged;

        /// <summary>
        /// 卷标名称
        /// </summary>
        public string VolumeLabel { get; set; } = "DonateDisk";

        /// <summary>
        /// 是否以只读模式共享
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// 当前活跃的文件句柄数
        /// </summary>
        public int ActiveHandleCount => _sessions.Count;

        /// <summary>
        /// 初始化磁盘操作服务
        /// </summary>
        /// <param name="pathResolver">路径安全沙箱解析器</param>
        /// <param name="volumeLabel">自定义卷标名称</param>
        /// <param name="isReadOnly">是否开启只读保护</param>
        public DiskOperationsService(SafePathResolver pathResolver, string? volumeLabel = null, bool isReadOnly = false)
        {
            _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
            if (!string.IsNullOrWhiteSpace(volumeLabel))
            {
                VolumeLabel = volumeLabel;
            }
            IsReadOnly = isReadOnly;

            // 初始化物理目录监视器
            _watcher = new FileSystemWatcher(_pathResolver.RootDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
            };

            _watcher.Created += (s, e) => OnWatcherEvent(FileChangeType.Created, e.FullPath);
            _watcher.Deleted += (s, e) => OnWatcherEvent(FileChangeType.Deleted, e.FullPath);
            _watcher.Changed += (s, e) => OnWatcherEvent(FileChangeType.Changed, e.FullPath);
            _watcher.Renamed += (s, e) => OnWatcherRenamed(e.OldFullPath, e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }

        private void OnWatcherEvent(FileChangeType changeType, string fullPath)
        {
            try
            {
                var rel = _pathResolver.GetRelativePath(fullPath);
                bool isDir = Directory.Exists(fullPath);
                FileChanged?.Invoke(this, new FileChangeEventDto
                {
                    ChangeType = changeType,
                    RelativePath = rel,
                    IsDirectory = isDir,
                    Timestamp = DateTime.UtcNow
                });
            }
            catch
            {
                // 忽略被监视目录外的路径解析异常
            }
        }

        private void OnWatcherRenamed(string oldFullPath, string newFullPath)
        {
            try
            {
                var oldRel = _pathResolver.GetRelativePath(oldFullPath);
                var newRel = _pathResolver.GetRelativePath(newFullPath);
                bool isDir = Directory.Exists(newFullPath);
                FileChanged?.Invoke(this, new FileChangeEventDto
                {
                    ChangeType = FileChangeType.Renamed,
                    RelativePath = newRel,
                    OldRelativePath = oldRel,
                    IsDirectory = isDir,
                    Timestamp = DateTime.UtcNow
                });
            }
            catch
            {
                // 忽略被监视目录外的路径解析异常
            }
        }

        /// <inheritdoc/>
        public DiskVolumeInfoDto GetVolumeInfo()
        {
            return new DiskVolumeInfoDto
            {
                VolumeLabel = VolumeLabel,
                FileSystemName = "NTFS",
                MaximumComponentLength = 256
            };
        }

        /// <inheritdoc/>
        public DiskFreeSpaceDto GetDiskFreeSpace()
        {
            try
            {
                var rootPath = _pathResolver.RootDirectory;
                var rootDirInfo = new DirectoryInfo(rootPath);
                var drive = new DriveInfo(rootDirInfo.Root.FullName);

                return new DiskFreeSpaceDto
                {
                    FreeBytesAvailable = drive.AvailableFreeSpace,
                    TotalNumberOfBytes = drive.TotalSize,
                    TotalNumberOfFreeBytes = drive.TotalFreeSpace
                };
            }
            catch
            {
                const long defaultSize = 100L * 1024 * 1024 * 1024;
                return new DiskFreeSpaceDto
                {
                    FreeBytesAvailable = defaultSize,
                    TotalNumberOfBytes = defaultSize,
                    TotalNumberOfFreeBytes = defaultSize
                };
            }
        }

        /// <inheritdoc/>
        public DiskItemInfoDto? GetPathInfo(string path)
        {
            var fullPath = _pathResolver.ResolveFullPath(path);

            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                var attrs = fi.Attributes;
                if (attrs == 0) attrs = FileAttributes.Normal;

                return new DiskItemInfoDto
                {
                    Name = fi.Name,
                    IsDirectory = false,
                    Length = fi.Length,
                    Attributes = attrs,
                    CreationTime = fi.CreationTimeUtc,
                    LastAccessTime = fi.LastAccessTimeUtc,
                    LastWriteTime = fi.LastWriteTimeUtc
                };
            }

            if (Directory.Exists(fullPath))
            {
                var di = new DirectoryInfo(fullPath);
                return new DiskItemInfoDto
                {
                    Name = di.Name,
                    IsDirectory = true,
                    Length = 0,
                    Attributes = di.Attributes | FileAttributes.Directory,
                    CreationTime = di.CreationTimeUtc,
                    LastAccessTime = di.LastAccessTimeUtc,
                    LastWriteTime = di.LastWriteTimeUtc
                };
            }

            return null;
        }

        /// <inheritdoc/>
        public List<DiskItemInfoDto> FindFiles(string path, string? searchPattern = null)
        {
            var fullPath = _pathResolver.ResolveFullPath(path);
            var results = new List<DiskItemInfoDto>();

            if (!Directory.Exists(fullPath))
            {
                return results;
            }

            var di = new DirectoryInfo(fullPath);
            var pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern;

            try
            {
                var entries = di.EnumerateFileSystemInfos(pattern, SearchOption.TopDirectoryOnly);
                foreach (var entry in entries)
                {
                    bool isDir = (entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                    long len = 0;
                    var attrs = entry.Attributes;
                    if (!isDir && entry is FileInfo fi)
                    {
                        len = fi.Length;
                        if (attrs == 0) attrs = FileAttributes.Normal;
                    }
                    else if (isDir)
                    {
                        attrs |= FileAttributes.Directory;
                    }

                    results.Add(new DiskItemInfoDto
                    {
                        Name = entry.Name,
                        IsDirectory = isDir,
                        Length = len,
                        Attributes = attrs,
                        CreationTime = entry.CreationTimeUtc,
                        LastAccessTime = entry.LastAccessTimeUtc,
                        LastWriteTime = entry.LastWriteTimeUtc
                    });
                }
            }
            catch (Exception)
            {
                // 忽略没有权限枚举的子目录
            }

            return results;
        }

        /// <inheritdoc/>
        public FileOpenResultDto OpenFile(string path, int access, int share, int mode, int options, int attributes)
        {
            var fullPath = _pathResolver.ResolveFullPath(path);

            // 若路径对应已有目录
            if (Directory.Exists(fullPath))
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    IsDirectory = true,
                    Length = 0,
                    NtStatusCode = 0 // Success
                };
            }

            var fileMode = (FileMode)mode;
            var fileOptions = (FileOptions)options;

            // 转换文件访问权限（安全规约掩码，支持标准与 Win32 访问掩码）
            FileAccess fileAccess = FileAccess.Read;
            if (access == (int)FileAccess.Write)
            {
                fileAccess = FileAccess.Write;
            }
            else if (access == (int)FileAccess.ReadWrite)
            {
                fileAccess = FileAccess.ReadWrite;
            }
            else
            {
                // 包含写入掩码 (GenericWrite 0x40000000, WriteData 2, AppendData 4, Delete 0x10000)
                bool hasWrite = (access & 0x40000000) != 0 || (access & 2) != 0 || (access & 4) != 0;
                // 包含读取掩码 (GenericRead 0x80000000, ReadData 1, Execute 0x20)
                bool hasRead = (access & unchecked((int)0x80000000)) != 0 || (access & 1) != 0 || (access & 0x20) != 0;

                if (hasWrite && hasRead) fileAccess = FileAccess.ReadWrite;
                else if (hasWrite) fileAccess = FileAccess.Write;
                else fileAccess = FileAccess.Read;
            }

            // 默认赋予共享读写及删除权限，避免 Windows 资源管理器多线程同时预览冲突
            FileShare fileShare = (FileShare)share | FileShare.ReadWrite | FileShare.Delete;

            // 只读保护模式拦截
            if (IsReadOnly && (fileAccess.HasFlag(FileAccess.Write) || fileMode == FileMode.Create || fileMode == FileMode.CreateNew || fileMode == FileMode.Truncate))
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    NtStatusCode = unchecked((int)0xC0000022), // STATUS_ACCESS_DENIED
                    ErrorMessage = "当前远程磁盘处于只读保护模式。"
                };
            }

            bool exists = File.Exists(fullPath);

            // 检查已存在冲突
            if (fileMode == FileMode.CreateNew && exists)
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    NtStatusCode = unchecked((int)0xC0000035), // STATUS_OBJECT_NAME_COLLISION (AlreadyExists)
                    ErrorMessage = "文件已存在。"
                };
            }

            // 检查文件不存在冲突
            if ((fileMode == FileMode.Open || fileMode == FileMode.Truncate) && !exists)
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    NtStatusCode = unchecked((int)0xC0000034), // STATUS_OBJECT_NAME_NOT_FOUND (FileNotFound)
                    ErrorMessage = "文件不存在。"
                };
            }

            try
            {
                // 确保上级父目录存在
                var parentDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir) &&
                    (fileMode == FileMode.Create || fileMode == FileMode.CreateNew || fileMode == FileMode.OpenOrCreate))
                {
                    Directory.CreateDirectory(parentDir);
                }

                var fs = new FileStream(fullPath, fileMode, fileAccess, fileShare, 4096, fileOptions);
                long handleId = System.Threading.Interlocked.Increment(ref _handleCounter);

                var session = new FileStreamSession(handleId, path, fs);
                _sessions[handleId] = session;

                // 若以 Create 或 OpenOrCreate 打开已存在的文件，在 Windows 内核返回 AlreadyExists 状态码代表打开了已有文件
                int statusCode = 0;
                if (exists && (fileMode == FileMode.Create || fileMode == FileMode.OpenOrCreate))
                {
                    statusCode = unchecked((int)0xC0000035); // STATUS_OBJECT_NAME_COLLISION (AlreadyExists)
                }

                return new FileOpenResultDto
                {
                    HandleId = handleId,
                    IsDirectory = false,
                    Length = fs.Length,
                    NtStatusCode = statusCode
                };
            }
            catch (FileNotFoundException)
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    NtStatusCode = unchecked((int)0xC0000034), // STATUS_OBJECT_NAME_NOT_FOUND
                    ErrorMessage = "文件不存在。"
                };
            }
            catch (DirectoryNotFoundException)
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    NtStatusCode = unchecked((int)0xC000003A), // STATUS_OBJECT_PATH_NOT_FOUND
                    ErrorMessage = "路径不存在。"
                };
            }
            catch (UnauthorizedAccessException)
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    NtStatusCode = unchecked((int)0xC0000022), // STATUS_ACCESS_DENIED
                    ErrorMessage = "拒绝访问。"
                };
            }
            catch (IOException ex)
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    NtStatusCode = unchecked((int)0xC0000043), // STATUS_SHARING_VIOLATION
                    ErrorMessage = ex.Message
                };
            }
            catch (Exception ex)
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    NtStatusCode = unchecked((int)0xC0000001), // STATUS_UNSUCCESSFUL
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <inheritdoc/>
        public byte[] ReadBlock(long handleId, long offset, int count)
        {
            if (_sessions.TryGetValue(handleId, out var session))
            {
                return session.ReadBlock(offset, count);
            }
            return Array.Empty<byte>();
        }

        /// <inheritdoc/>
        public int WriteBlock(long handleId, long offset, byte[] data)
        {
            if (IsReadOnly)
            {
                throw new UnauthorizedAccessException("远程磁盘处于只读模式。");
            }

            if (_sessions.TryGetValue(handleId, out var session))
            {
                return session.WriteBlock(offset, data);
            }
            return 0;
        }

        /// <inheritdoc/>
        public void Flush(long handleId)
        {
            if (_sessions.TryGetValue(handleId, out var session))
            {
                session.Flush();
            }
        }

        /// <inheritdoc/>
        public void SetEndOfFile(long handleId, long length)
        {
            if (IsReadOnly)
            {
                throw new UnauthorizedAccessException("远程磁盘处于只读模式。");
            }

            if (_sessions.TryGetValue(handleId, out var session))
            {
                session.SetLength(length);
            }
        }

        /// <inheritdoc/>
        public void CloseFile(long handleId)
        {
            if (_sessions.TryRemove(handleId, out var session))
            {
                session.Dispose();
            }
        }

        /// <inheritdoc/>
        public void CreateDirectory(string path)
        {
            if (IsReadOnly) throw new UnauthorizedAccessException("只读模式禁止创建目录。");
            var fullPath = _pathResolver.ResolveFullPath(path);
            Directory.CreateDirectory(fullPath);
        }

        private bool IsRootDirectory(string fullPath)
        {
            var trimmedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var trimmedRoot = _pathResolver.RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(trimmedFull, trimmedRoot, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc/>
        public void DeleteFile(string path)
        {
            if (IsReadOnly) throw new UnauthorizedAccessException("只读模式禁止删除文件。");
            var fullPath = _pathResolver.ResolveFullPath(path);
            if (IsRootDirectory(fullPath))
            {
                throw new UnauthorizedAccessException("禁止删除根目录。");
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }

        /// <inheritdoc/>
        public void DeleteDirectory(string path)
        {
            if (IsReadOnly) throw new UnauthorizedAccessException("只读模式禁止删除目录。");
            var fullPath = _pathResolver.ResolveFullPath(path);
            if (IsRootDirectory(fullPath))
            {
                throw new UnauthorizedAccessException("禁止删除根目录。");
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }

        /// <inheritdoc/>
        public void MoveFile(string oldPath, string newPath, bool replaceExisting)
        {
            if (IsReadOnly) throw new UnauthorizedAccessException("只读模式禁止重命名或移动。");
            var oldFull = _pathResolver.ResolveFullPath(oldPath);
            var newFull = _pathResolver.ResolveFullPath(newPath);

            if (IsRootDirectory(oldFull))
            {
                throw new UnauthorizedAccessException("禁止重命名或移动根目录。");
            }

            if (File.Exists(oldFull))
            {
                if (File.Exists(newFull))
                {
                    if (replaceExisting)
                    {
                        File.Delete(newFull);
                    }
                    else
                    {
                        throw new IOException("目标文件已存在。");
                    }
                }
                File.Move(oldFull, newFull);
            }
            else if (Directory.Exists(oldFull))
            {
                Directory.Move(oldFull, newFull);
            }
            else
            {
                throw new FileNotFoundException("源文件或目录不存在。");
            }
        }

        /// <inheritdoc/>
        public void SetFileAttributes(string path, int attributes)
        {
            if (IsReadOnly) return;
            var fullPath = _pathResolver.ResolveFullPath(path);
            if (File.Exists(fullPath))
            {
                File.SetAttributes(fullPath, (FileAttributes)attributes);
            }
            else if (Directory.Exists(fullPath))
            {
                var di = new DirectoryInfo(fullPath);
                di.Attributes = (FileAttributes)attributes;
            }
        }

        /// <inheritdoc/>
        public void SetFileTime(string path, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime)
        {
            if (IsReadOnly) return;
            var fullPath = _pathResolver.ResolveFullPath(path);
            if (File.Exists(fullPath))
            {
                if (creationTime.HasValue) File.SetCreationTimeUtc(fullPath, creationTime.Value);
                if (lastAccessTime.HasValue) File.SetLastAccessTimeUtc(fullPath, lastAccessTime.Value);
                if (lastWriteTime.HasValue) File.SetLastWriteTimeUtc(fullPath, lastWriteTime.Value);
            }
            else if (Directory.Exists(fullPath))
            {
                if (creationTime.HasValue) Directory.SetCreationTimeUtc(fullPath, creationTime.Value);
                if (lastAccessTime.HasValue) Directory.SetLastAccessTimeUtc(fullPath, lastAccessTime.Value);
                if (lastWriteTime.HasValue) Directory.SetLastWriteTimeUtc(fullPath, lastWriteTime.Value);
            }
        }

        /// <summary>
        /// 释放所有未关闭的流会话（通常在断线或停止服务时调用）
        /// </summary>
        public void DisposeAllSessions()
        {
            foreach (var kv in _sessions)
            {
                if (_sessions.TryRemove(kv.Key, out var session))
                {
                    session.Dispose();
                }
            }
        }

        /// <summary>
        /// 释放服务资源，停止文件系统监听并释放流
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            catch { }

            DisposeAllSessions();
        }
    }
}
