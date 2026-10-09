using System;
using System.IO;

namespace ApeFree.DonateDisk.Contracts
{
    /// <summary>
    /// 文件或目录的元数据信息传输对象
    /// </summary>
    public class DiskItemInfoDto
    {
        /// <summary>
        /// 文件或目录名称（不含父目录路径）
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 是否为目录
        /// </summary>
        public bool IsDirectory { get; set; }

        /// <summary>
        /// 文件大小（字节）
        /// </summary>
        public long Length { get; set; }

        /// <summary>
        /// 文件属性标志（只读、隐藏、系统、归档等）
        /// </summary>
        public FileAttributes Attributes { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime? CreationTime { get; set; }

        /// <summary>
        /// 最后访问时间
        /// </summary>
        public DateTime? LastAccessTime { get; set; }

        /// <summary>
        /// 最后写入/修改时间
        /// </summary>
        public DateTime? LastWriteTime { get; set; }
    }

    /// <summary>
    /// 磁盘卷标与文件系统信息传输对象
    /// </summary>
    public class DiskVolumeInfoDto
    {
        /// <summary>
        /// 卷标名称（如 "DonateDisk-Alice"）
        /// </summary>
        public string VolumeLabel { get; set; } = "DonateDisk";

        /// <summary>
        /// 文件系统名称（如 "NTFS" 或 "FAT32"）
        /// </summary>
        public string FileSystemName { get; set; } = "NTFS";

        /// <summary>
        /// 路径组成部分的最大长度（默认 256）
        /// </summary>
        public uint MaximumComponentLength { get; set; } = 256;
    }

    /// <summary>
    /// 磁盘容量与剩余空间传输对象
    /// </summary>
    public class DiskFreeSpaceDto
    {
        /// <summary>
        /// 当前用户可用空间（字节）
        /// </summary>
        public long FreeBytesAvailable { get; set; }

        /// <summary>
        /// 磁盘总容量（字节）
        /// </summary>
        public long TotalNumberOfBytes { get; set; }

        /// <summary>
        /// 磁盘总空闲空间（字节）
        /// </summary>
        public long TotalNumberOfFreeBytes { get; set; }
    }

    /// <summary>
    /// 打开文件结果传输对象
    /// </summary>
    public class FileOpenResultDto
    {
        /// <summary>
        /// 在 Slave 端分配的远程流句柄标识符（0 表示目录或失败）
        /// </summary>
        public long HandleId { get; set; }

        /// <summary>
        /// 目标是否为目录
        /// </summary>
        public bool IsDirectory { get; set; }

        /// <summary>
        /// 文件长度
        /// </summary>
        public long Length { get; set; }

        /// <summary>
        /// Windows NT 状态码（0 代表 NtStatus.Success）
        /// </summary>
        public int NtStatusCode { get; set; }

        /// <summary>
        /// 错误消息（若失败）
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}
