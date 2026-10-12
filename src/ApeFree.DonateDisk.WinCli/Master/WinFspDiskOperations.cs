using ApeFree.DonateDisk.Contracts;
using Fsp;
using Fsp.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace ApeFree.DonateDisk.Master
{
    /// <summary>
    /// 将 Windows 内核文件系统 I/O 请求桥接至远程 Slave 端 RPC 接口的 WinFsp (FileSystemBase) 实现。
    /// 遵循 Windows NT 文件系统规范，安全映射 Win32 访问掩码与文件生命周期。
    /// </summary>
    public class WinFspDiskOperations : FileSystemBase, IDisposable
    {
        private readonly IDiskOperationsService _remote;
        private readonly MetadataCache _cache;
        private readonly string _defaultVolumeLabel;
        private bool _isDisposed;

        /// <summary>
        /// 当监听到远程 Slave 目录文件发生变动时触发
        /// </summary>
        public event EventHandler<FileChangeEventDto>? FileChanged;

        /// <summary>
        /// 文件或目录的内部上下文描述符
        /// </summary>
        public sealed class FileContext
        {
            public long HandleId { get; set; }
            public bool IsDirectory { get; set; }
            public string Path { get; set; } = string.Empty;
        }

        private const ulong AllocationUnit = 4096;

        /// <summary>
        /// 构造 WinFspDiskOperations 桥接器
        /// </summary>
        /// <param name="remote">远程磁盘 RPC 代理对象</param>
        /// <param name="cache">元数据缓存（可选）</param>
        /// <param name="defaultVolumeLabel">默认卷标名</param>
        public WinFspDiskOperations(IDiskOperationsService remote, MetadataCache? cache = null, string? defaultVolumeLabel = null)
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

        #region 内部辅助方法

        public DiskItemInfoDto? QueryItemInfo(string fileName)
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

        public static Fsp.Interop.FileInfo MapToFileInfo(DiskItemInfoDto item)
        {
            var attrs = (uint)item.Attributes;
            if (item.IsDirectory)
            {
                attrs |= (uint)FileAttributes.Directory;
            }
            else if (attrs == 0)
            {
                attrs = (uint)FileAttributes.Normal;
            }

            ulong fileSize = (ulong)Math.Max(0, item.Length);
            ulong allocSize = (fileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;

            ulong ct = (ulong)(item.CreationTime?.ToFileTimeUtc() ?? DateTime.UtcNow.ToFileTimeUtc());
            ulong lat = (ulong)(item.LastAccessTime?.ToFileTimeUtc() ?? DateTime.UtcNow.ToFileTimeUtc());
            ulong lwt = (ulong)(item.LastWriteTime?.ToFileTimeUtc() ?? DateTime.UtcNow.ToFileTimeUtc());

            return new Fsp.Interop.FileInfo
            {
                FileAttributes = attrs,
                ReparseTag = 0,
                FileSize = fileSize,
                AllocationSize = allocSize,
                CreationTime = ct,
                LastAccessTime = lat,
                LastWriteTime = lwt,
                ChangeTime = lwt,
                IndexNumber = 0,
                HardLinks = 0
            };
        }

        public static Fsp.Interop.FileInfo CreateDirectoryFileInfo()
        {
            var now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            return new Fsp.Interop.FileInfo
            {
                FileAttributes = (uint)FileAttributes.Directory,
                ReparseTag = 0,
                FileSize = 0,
                AllocationSize = 0,
                CreationTime = now,
                LastAccessTime = now,
                LastWriteTime = now,
                ChangeTime = now,
                IndexNumber = 0,
                HardLinks = 0
            };
        }

        #endregion

        #region WinFsp FileSystemBase 回调实现

        /// <inheritdoc/>
        public override int Init(object Host)
        {
            if (Host is FileSystemHost host)
            {
                host.SectorSize = 512;
                host.SectorsPerAllocationUnit = 8;
                host.MaxComponentLength = 255;
                host.CasePreservedNames = true;
                host.UnicodeOnDisk = true;
                host.PassQueryDirectoryPattern = true;
            }
            return STATUS_SUCCESS;
        }

        /// <inheritdoc/>
        public override int GetVolumeInfo(out VolumeInfo VolumeInfo)
        {
            VolumeInfo = default;
            try
            {
                var space = _remote.GetDiskFreeSpace();
                VolumeInfo.TotalSize = (ulong)space.TotalNumberOfBytes;
                VolumeInfo.FreeSize = (ulong)space.FreeBytesAvailable;
                return STATUS_SUCCESS;
            }
            catch
            {
                const ulong defaultSize = 100UL * 1024 * 1024 * 1024;
                VolumeInfo.TotalSize = defaultSize;
                VolumeInfo.FreeSize = defaultSize;
                return STATUS_SUCCESS;
            }
        }

        /// <inheritdoc/>
        public override int SetVolumeLabel(string VolumeLabel, out VolumeInfo VolumeInfo)
        {
            return GetVolumeInfo(out VolumeInfo);
        }

        /// <inheritdoc/>
        public override int GetSecurityByName(string FileName, out uint FileAttributes, ref byte[] SecurityDescriptor)
        {
            if (FileName == "\\" || FileName == "/")
            {
                FileAttributes = (uint)System.IO.FileAttributes.Directory;
                return STATUS_SUCCESS;
            }

            var item = QueryItemInfo(FileName);
            if (item == null)
            {
                FileAttributes = 0;
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            var attrs = (uint)item.Attributes;
            if (item.IsDirectory)
            {
                attrs |= (uint)System.IO.FileAttributes.Directory;
            }
            else if (attrs == 0)
            {
                attrs = (uint)System.IO.FileAttributes.Normal;
            }

            FileAttributes = attrs;
            return STATUS_SUCCESS;
        }

        /// <inheritdoc/>
        public override int Open(
            string FileName,
            uint CreateOptions,
            uint GrantedAccess,
            out object FileNode,
            out object FileDesc,
            out Fsp.Interop.FileInfo FileInfo,
            out string NormalizedName)
        {
            FileNode = FileName;
            NormalizedName = null!;

            if (FileName == "\\" || FileName == "/")
            {
                FileDesc = new FileContext { IsDirectory = true, Path = "\\" };
                FileInfo = CreateDirectoryFileInfo();
                return STATUS_SUCCESS;
            }

            var item = QueryItemInfo(FileName);
            if (item == null)
            {
                FileDesc = null!;
                FileInfo = default;
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            if (item.IsDirectory)
            {
                if ((CreateOptions & FILE_NON_DIRECTORY_FILE) != 0)
                {
                    FileDesc = null!;
                    FileInfo = default;
                    return STATUS_FILE_IS_A_DIRECTORY;
                }

                FileDesc = new FileContext { IsDirectory = true, Path = FileName };
                FileInfo = MapToFileInfo(item);
                return STATUS_SUCCESS;
            }

            if ((CreateOptions & FILE_DIRECTORY_FILE) != 0)
            {
                FileDesc = null!;
                FileInfo = default;
                return STATUS_NOT_A_DIRECTORY;
            }

            // 判断读写权限
            bool write = (GrantedAccess & (0x0002 | 0x0004 | 0x00010000 | 0x40000000)) != 0;
            var sysAccess = write ? FileAccess.ReadWrite : FileAccess.Read;
            var openResult = _remote.OpenFile(FileName, (int)sysAccess, (int)(FileShare.ReadWrite | FileShare.Delete), (int)FileMode.Open, 0, (int)item.Attributes);
            if (openResult.NtStatusCode != 0)
            {
                FileDesc = null!;
                FileInfo = default;
                return openResult.NtStatusCode;
            }

            FileDesc = new FileContext { HandleId = openResult.HandleId, IsDirectory = false, Path = FileName };
            FileInfo = MapToFileInfo(item);
            return STATUS_SUCCESS;
        }

        /// <inheritdoc/>
        public override int Create(
            string FileName,
            uint CreateOptions,
            uint GrantedAccess,
            uint FileAttributes,
            byte[] SecurityDescriptor,
            ulong AllocationSize,
            out object FileNode,
            out object FileDesc,
            out Fsp.Interop.FileInfo FileInfo,
            out string NormalizedName)
        {
            FileNode = FileName;
            NormalizedName = null!;

            bool isDirectory = (CreateOptions & FILE_DIRECTORY_FILE) != 0 || ((System.IO.FileAttributes)FileAttributes).HasFlag(System.IO.FileAttributes.Directory);
            if (isDirectory)
            {
                try
                {
                    _remote.CreateDirectory(FileName);
                    _cache.Invalidate(FileName);
                    var item = QueryItemInfo(FileName);
                    FileDesc = new FileContext { IsDirectory = true, Path = FileName };
                    FileInfo = item != null ? MapToFileInfo(item) : CreateDirectoryFileInfo();
                    return STATUS_SUCCESS;
                }
                catch
                {
                    FileDesc = null!;
                    FileInfo = default;
                    return STATUS_ACCESS_DENIED;
                }
            }
            else
            {
                var openResult = _remote.OpenFile(FileName, (int)FileAccess.ReadWrite, (int)(FileShare.ReadWrite | FileShare.Delete), (int)FileMode.CreateNew, 0, (int)FileAttributes);
                if (openResult.NtStatusCode != 0)
                {
                    FileDesc = null!;
                    FileInfo = default;
                    return openResult.NtStatusCode;
                }

                _cache.Invalidate(FileName);
                var item = QueryItemInfo(FileName);
                FileDesc = new FileContext { HandleId = openResult.HandleId, IsDirectory = false, Path = FileName };
                FileInfo = item != null ? MapToFileInfo(item) : new Fsp.Interop.FileInfo { FileAttributes = FileAttributes };
                return STATUS_SUCCESS;
            }
        }

        /// <inheritdoc/>
        public override int Overwrite(
            object FileNode,
            object FileDesc,
            uint FileAttributes,
            bool ReplaceFileAttributes,
            ulong AllocationSize,
            out Fsp.Interop.FileInfo FileInfo)
        {
            FileInfo = default;
            if (FileDesc is FileContext ctx && ctx.HandleId > 0)
            {
                try
                {
                    _remote.SetEndOfFile(ctx.HandleId, 0);
                    _cache.Invalidate(ctx.Path);
                    var item = QueryItemInfo(ctx.Path);
                    if (item != null) FileInfo = MapToFileInfo(item);
                    return STATUS_SUCCESS;
                }
                catch
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
            }
            return STATUS_INVALID_HANDLE;
        }

        /// <inheritdoc/>
        public override void Cleanup(object FileNode, object FileDesc, string FileName, uint Flags)
        {
            if ((Flags & CleanupDelete) != 0 && FileDesc is FileContext ctx)
            {
                try
                {
                    if (ctx.HandleId > 0)
                    {
                        _remote.CloseFile(ctx.HandleId);
                        ctx.HandleId = 0;
                    }

                    if (ctx.IsDirectory)
                    {
                        _remote.DeleteDirectory(FileName);
                    }
                    else
                    {
                        _remote.DeleteFile(FileName);
                    }
                    _cache.Invalidate(FileName);
                }
                catch
                {
                    // 忽略清理异常
                }
            }
        }

        /// <inheritdoc/>
        public override void Close(object FileNode, object FileDesc)
        {
            if (FileDesc is FileContext ctx && ctx.HandleId > 0)
            {
                try
                {
                    _remote.CloseFile(ctx.HandleId);
                }
                catch
                {
                }
                ctx.HandleId = 0;
            }
        }

        /// <inheritdoc/>
        public override int Read(
            object FileNode,
            object FileDesc,
            IntPtr Buffer,
            ulong Offset,
            uint Length,
            out uint BytesTransferred)
        {
            BytesTransferred = 0;
            if (FileDesc is FileContext ctx && ctx.HandleId > 0)
            {
                try
                {
                    var data = _remote.ReadBlock(ctx.HandleId, (long)Offset, (int)Length);
                    if (data != null && data.Length > 0)
                    {
                        Marshal.Copy(data, 0, Buffer, data.Length);
                        BytesTransferred = (uint)data.Length;
                    }
                    return STATUS_SUCCESS;
                }
                catch
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
            }
            return STATUS_INVALID_HANDLE;
        }

        /// <inheritdoc/>
        public override int Write(
            object FileNode,
            object FileDesc,
            IntPtr Buffer,
            ulong Offset,
            uint Length,
            bool WriteToEndOfFile,
            bool ConstrainedIo,
            out uint BytesTransferred,
            out Fsp.Interop.FileInfo FileInfo)
        {
            BytesTransferred = 0;
            FileInfo = default;
            if (FileDesc is FileContext ctx && ctx.HandleId > 0)
            {
                try
                {
                    byte[] managedBuf = new byte[Length];
                    Marshal.Copy(Buffer, managedBuf, 0, (int)Length);
                    long writeOffset = WriteToEndOfFile ? -1 : (long)Offset;
                    int written = _remote.WriteBlock(ctx.HandleId, writeOffset, managedBuf);
                    BytesTransferred = (uint)written;
                    _cache.Invalidate(ctx.Path);

                    var item = QueryItemInfo(ctx.Path);
                    if (item != null)
                    {
                        FileInfo = MapToFileInfo(item);
                    }
                    return STATUS_SUCCESS;
                }
                catch (UnauthorizedAccessException)
                {
                    return STATUS_ACCESS_DENIED;
                }
                catch
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
            }
            return STATUS_INVALID_HANDLE;
        }

        /// <inheritdoc/>
        public override int Flush(object FileNode, object FileDesc, out Fsp.Interop.FileInfo FileInfo)
        {
            FileInfo = default;
            if (FileDesc is FileContext ctx && ctx.HandleId > 0)
            {
                try
                {
                    _remote.Flush(ctx.HandleId);
                    var item = QueryItemInfo(ctx.Path);
                    if (item != null)
                    {
                        FileInfo = MapToFileInfo(item);
                    }
                    return STATUS_SUCCESS;
                }
                catch
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
            }
            return STATUS_SUCCESS;
        }

        /// <inheritdoc/>
        public override int GetFileInfo(object FileNode, object FileDesc, out Fsp.Interop.FileInfo FileInfo)
        {
            if (FileDesc is FileContext ctx)
            {
                if (ctx.IsDirectory && (ctx.Path == "\\" || ctx.Path == "/"))
                {
                    FileInfo = CreateDirectoryFileInfo();
                    return STATUS_SUCCESS;
                }

                var item = QueryItemInfo(ctx.Path);
                if (item != null)
                {
                    FileInfo = MapToFileInfo(item);
                    return STATUS_SUCCESS;
                }
            }

            FileInfo = default;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        /// <inheritdoc/>
        public override int SetBasicInfo(
            object FileNode,
            object FileDesc,
            uint FileAttributes,
            ulong CreationTime,
            ulong LastAccessTime,
            ulong LastWriteTime,
            ulong ChangeTime,
            out Fsp.Interop.FileInfo FileInfo)
        {
            FileInfo = default;
            if (FileDesc is FileContext ctx)
            {
                try
                {
                    DateTime? ct = CreationTime != 0 ? DateTime.FromFileTimeUtc((long)CreationTime) : null;
                    DateTime? lat = LastAccessTime != 0 ? DateTime.FromFileTimeUtc((long)LastAccessTime) : null;
                    DateTime? lwt = LastWriteTime != 0 ? DateTime.FromFileTimeUtc((long)LastWriteTime) : null;

                    if (ct.HasValue || lat.HasValue || lwt.HasValue)
                    {
                        _remote.SetFileTime(ctx.Path, ct, lat, lwt);
                    }
                    if (FileAttributes != 0 && FileAttributes != unchecked((uint)-1))
                    {
                        _remote.SetFileAttributes(ctx.Path, (int)FileAttributes);
                    }

                    _cache.Invalidate(ctx.Path);
                    var item = QueryItemInfo(ctx.Path);
                    if (item != null)
                    {
                        FileInfo = MapToFileInfo(item);
                    }
                    return STATUS_SUCCESS;
                }
                catch (UnauthorizedAccessException)
                {
                    return STATUS_ACCESS_DENIED;
                }
                catch
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
            }
            return STATUS_INVALID_HANDLE;
        }

        /// <inheritdoc/>
        public override int SetFileSize(
            object FileNode,
            object FileDesc,
            ulong NewSize,
            bool SetAllocationSize,
            out Fsp.Interop.FileInfo FileInfo)
        {
            FileInfo = default;
            if (FileDesc is FileContext ctx && ctx.HandleId > 0)
            {
                try
                {
                    _remote.SetEndOfFile(ctx.HandleId, (long)NewSize);
                    _cache.Invalidate(ctx.Path);
                    var item = QueryItemInfo(ctx.Path);
                    if (item != null)
                    {
                        FileInfo = MapToFileInfo(item);
                    }
                    return STATUS_SUCCESS;
                }
                catch (UnauthorizedAccessException)
                {
                    return STATUS_ACCESS_DENIED;
                }
                catch
                {
                    return STATUS_UNEXPECTED_IO_ERROR;
                }
            }
            return STATUS_INVALID_HANDLE;
        }

        /// <inheritdoc/>
        public override int CanDelete(object FileNode, object FileDesc, string FileName)
        {
            var item = QueryItemInfo(FileName);
            if (item == null) return STATUS_OBJECT_NAME_NOT_FOUND;
            if (item.IsDirectory)
            {
                var children = _remote.FindFiles(FileName, null);
                if (children != null && children.Count > 0)
                {
                    return STATUS_DIRECTORY_NOT_EMPTY;
                }
            }
            return STATUS_SUCCESS;
        }

        /// <inheritdoc/>
        public override int Rename(
            object FileNode,
            object FileDesc,
            string FileName,
            string NewFileName,
            bool ReplaceIfExists)
        {
            try
            {
                if (FileDesc is FileContext ctx && ctx.HandleId > 0)
                {
                    try
                    {
                        _remote.CloseFile(ctx.HandleId);
                    }
                    catch { }
                    ctx.HandleId = 0;
                }

                _remote.MoveFile(FileName, NewFileName, ReplaceIfExists);
                _cache.Invalidate(FileName);
                _cache.Invalidate(NewFileName);
                return STATUS_SUCCESS;
            }
            catch (FileNotFoundException)
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }
            catch (UnauthorizedAccessException)
            {
                return STATUS_ACCESS_DENIED;
            }
            catch (IOException)
            {
                return STATUS_OBJECT_NAME_COLLISION;
            }
            catch
            {
                return STATUS_UNEXPECTED_IO_ERROR;
            }
        }

        /// <inheritdoc/>
        public override bool ReadDirectoryEntry(
            object FileNode,
            object FileDesc,
            string Pattern,
            string Marker,
            ref object Context,
            out string FileName,
            out Fsp.Interop.FileInfo FileInfo)
        {
            FileName = null!;
            FileInfo = default;

            if (FileDesc is not FileContext ctx || !ctx.IsDirectory)
            {
                return false;
            }

            var enumerator = Context as IEnumerator<KeyValuePair<string, Fsp.Interop.FileInfo>>;
            if (enumerator == null)
            {
                var entries = new List<KeyValuePair<string, Fsp.Interop.FileInfo>>
                {
                    new KeyValuePair<string, Fsp.Interop.FileInfo>(".", CreateDirectoryFileInfo()),
                    new KeyValuePair<string, Fsp.Interop.FileInfo>("..", CreateDirectoryFileInfo())
                };

                try
                {
                    var items = _remote.FindFiles(ctx.Path, Pattern);
                    _cache.SetBatch(ctx.Path, items);
                    foreach (var item in items)
                    {
                        entries.Add(new KeyValuePair<string, Fsp.Interop.FileInfo>(item.Name, MapToFileInfo(item)));
                    }
                }
                catch
                {
                }

                if (!string.IsNullOrEmpty(Marker))
                {
                    int markerIdx = entries.FindIndex(e => string.Equals(e.Key, Marker, StringComparison.OrdinalIgnoreCase));
                    if (markerIdx >= 0)
                    {
                        entries = entries.Skip(markerIdx + 1).ToList();
                    }
                }

                enumerator = entries.GetEnumerator();
                Context = enumerator;
            }

            if (enumerator.MoveNext())
            {
                FileName = enumerator.Current.Key;
                FileInfo = enumerator.Current.Value;
                return true;
            }

            return false;
        }

        #endregion
    }
}
