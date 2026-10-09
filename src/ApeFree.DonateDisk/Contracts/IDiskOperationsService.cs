using ApeFree.ApeRpc.Attributes;
using System;
using System.Collections.Generic;

namespace ApeFree.DonateDisk.Contracts
{
    /// <summary>
    /// 远程虚拟磁盘操作服务契约。
    /// 由 Slave 端实现并暴露，Master 端通过 ApeRpc 动态代理获取其实例并分派 Dokan 文件系统调用。
    /// </summary>
    public interface IDiskOperationsService
    {
        /// <summary>
        /// 远程文件系统变动事件（当 Slave 端内部文件发生新建、修改、移动、删除等变动时主动推送给 Master 端）
        /// </summary>
        [RpcEvent(IsForcedSubscription = true)]
        event EventHandler<FileChangeEventDto> FileChanged;

        /// <summary>
        /// 获取共享卷标与文件系统信息
        /// </summary>
        DiskVolumeInfoDto GetVolumeInfo();

        /// <summary>
        /// 获取共享目录所在物理磁盘的可用空间
        /// </summary>
        DiskFreeSpaceDto GetDiskFreeSpace();

        /// <summary>
        /// 查询指定相对路径（文件或目录）的元数据信息
        /// </summary>
        /// <param name="path">相对于共享根目录的路径</param>
        DiskItemInfoDto? GetPathInfo(string path);

        /// <summary>
        /// 遍历指定目录下的子文件与子目录列表
        /// </summary>
        /// <param name="path">相对目录路径</param>
        /// <param name="searchPattern">可选搜索模式匹配表达式</param>
        List<DiskItemInfoDto> FindFiles(string path, string? searchPattern = null);

        /// <summary>
        /// 打开或创建指定文件/目录并分配远程文件流句柄
        /// </summary>
        /// <param name="path">相对文件路径</param>
        /// <param name="access">请求访问权限标志</param>
        /// <param name="share">共享模式</param>
        /// <param name="mode">创建/打开模式</param>
        /// <param name="options">文件选项</param>
        /// <param name="attributes">文件属性</param>
        FileOpenResultDto OpenFile(string path, int access, int share, int mode, int options, int attributes);

        /// <summary>
        /// 分块读取远程文件数据
        /// </summary>
        /// <param name="handleId">打开的文件句柄标识符</param>
        /// <param name="offset">读取起始偏移量</param>
        /// <param name="count">请求读取的字节数</param>
        [RpcTimeout(30000)]
        byte[] ReadBlock(long handleId, long offset, int count);

        /// <summary>
        /// 分块写入数据到远程文件
        /// </summary>
        /// <param name="handleId">打开的文件句柄标识符</param>
        /// <param name="offset">写入起始偏移量</param>
        /// <param name="data">待写入的字节缓冲区</param>
        /// <returns>实际写入的字节数</returns>
        [RpcTimeout(30000)]
        int WriteBlock(long handleId, long offset, byte[] data);

        /// <summary>
        /// 刷新文件缓冲区到磁盘
        /// </summary>
        /// <param name="handleId">文件句柄标识符</param>
        void Flush(long handleId);

        /// <summary>
        /// 设置文件截断或扩展长度
        /// </summary>
        /// <param name="handleId">文件句柄标识符</param>
        /// <param name="length">新文件长度</param>
        void SetEndOfFile(long handleId, long length);

        /// <summary>
        /// 关闭已打开的远程文件句柄并释放物理流资源
        /// </summary>
        /// <param name="handleId">文件句柄标识符</param>
        void CloseFile(long handleId);

        /// <summary>
        /// 创建新目录
        /// </summary>
        /// <param name="path">相对目录路径</param>
        void CreateDirectory(string path);

        /// <summary>
        /// 删除文件
        /// </summary>
        /// <param name="path">相对文件路径</param>
        void DeleteFile(string path);

        /// <summary>
        /// 删除目录
        /// </summary>
        /// <param name="path">相对目录路径</param>
        void DeleteDirectory(string path);

        /// <summary>
        /// 移动或重命名文件/目录
        /// </summary>
        /// <param name="oldPath">原始相对路径</param>
        /// <param name="newPath">目标相对路径</param>
        /// <param name="replaceExisting">是否覆盖已存在的目标文件</param>
        void MoveFile(string oldPath, string newPath, bool replaceExisting);

        /// <summary>
        /// 设置文件或目录的属性标志
        /// </summary>
        /// <param name="path">相对路径</param>
        /// <param name="attributes">文件属性</param>
        void SetFileAttributes(string path, int attributes);

        /// <summary>
        /// 设置文件或目录的时间戳
        /// </summary>
        /// <param name="path">相对路径</param>
        /// <param name="creationTime">创建时间</param>
        /// <param name="lastAccessTime">最后访问时间</param>
        /// <param name="lastWriteTime">最后写入时间</param>
        void SetFileTime(string path, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime);
    }
}
