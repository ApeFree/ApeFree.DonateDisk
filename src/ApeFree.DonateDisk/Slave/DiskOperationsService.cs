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
    /// </summary>
    public class DiskOperationsService : IDiskOperationsService, IDisposable
    {
        private readonly SafePathResolver _pathResolver;
        private readonly ConcurrentDictionary<long, FileStreamSession> _sessions = new ConcurrentDictionary<long, FileStreamSession>();
        private long _handleCounter = 100;
        private bool _isDisposed;

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
                // 回退假数据（默认 100GB）以防非卷设备报错
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
                return new DiskItemInfoDto
                {
                    Name = fi.Name,
                    IsDirectory = false,
                    Length = fi.Length,
                    Attributes = fi.Attributes,
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
                    Attributes = di.Attributes,
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
                    if (!isDir && entry is FileInfo fi)
                    {
                        len = fi.Length;
                    }

                    results.Add(new DiskItemInfoDto
                    {
                        Name = entry.Name,
                        IsDirectory = isDir,
                        Length = len,
                        Attributes = entry.Attributes,
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

            // 若路径对应目录或以反斜杠结尾且物理目录存在
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
            var fileAccess = (FileAccess)access;
            var fileShare = (FileShare)share;

            // 只读保护拦截
            if (IsReadOnly && (fileAccess.HasFlag(FileAccess.Write) || fileMode == FileMode.Create || fileMode == FileMode.CreateNew || fileMode == FileMode.Truncate))
            {
                return new FileOpenResultDto
                {
                    HandleId = 0,
                    NtStatusCode = unchecked((int)0xC0000022), // STATUS_ACCESS_DENIED
                    ErrorMessage = "当前远程磁盘处于只读保护模式。"
                };
            }

            try
            {
                // 确保上级目录存在
                var parentDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir) && (fileMode == FileMode.Create || fileMode == FileMode.CreateNew || fileMode == FileMode.OpenOrCreate))
                {
                    Directory.CreateDirectory(parentDir);
                }

                var fs = new FileStream(fullPath, fileMode, fileAccess, fileShare);
                long handleId = System.Threading.Interlocked.Increment(ref _handleCounter);

                var session = new FileStreamSession(handleId, path, fs);
                _sessions[handleId] = session;

                return new FileOpenResultDto
                {
                    HandleId = handleId,
                    IsDirectory = false,
                    Length = fs.Length,
                    NtStatusCode = 0 // Success
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

        /// <inheritdoc/>
        public void DeleteFile(string path)
        {
            if (IsReadOnly) throw new UnauthorizedAccessException("只读模式禁止删除文件。");
            var fullPath = _pathResolver.ResolveFullPath(path);
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

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            DisposeAllSessions();
        }
    }
}
