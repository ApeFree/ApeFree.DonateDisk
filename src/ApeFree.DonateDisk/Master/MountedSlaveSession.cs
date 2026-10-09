using ApeFree.DonateDisk.Contracts;
using DokanNet;
using STTech.BytesIO.Core;
using System;

namespace ApeFree.DonateDisk.Master
{
    /// <summary>
    /// 代表一个已挂载并正在运行的 Slave 虚拟磁盘会话
    /// </summary>
    public class MountedSlaveSession : IDisposable
    {
        private readonly DriveLetterManager _driveLetterManager;
        private bool _isDisposed;

        /// <summary>
        /// 关联的底层网络通信客户端
        /// </summary>
        public BytesClient Client { get; }

        /// <summary>
        /// 分配到的逻辑驱动器盘符字符（例如 'Z'）
        /// </summary>
        public char DriveLetter { get; }

        /// <summary>
        /// 挂载点路径（例如 "Z:\"）
        /// </summary>
        public string MountPoint { get; }

        /// <summary>
        /// Dokany 运行实例
        /// </summary>
        public DokanInstance? DokanInstance { get; set; }

        /// <summary>
        /// 虚拟磁盘操作桥接器
        /// </summary>
        public DokanDiskOperations Operations { get; }

        /// <summary>
        /// 远程磁盘 RPC 代理对象
        /// </summary>
        public IDiskOperationsService RemoteDiskService { get; }

        /// <summary>
        /// 卷信息
        /// </summary>
        public DiskVolumeInfoDto VolumeInfo { get; }

        /// <summary>
        /// 挂载时间
        /// </summary>
        public DateTime MountedTime { get; }

        public MountedSlaveSession(
            BytesClient client,
            char driveLetter,
            string mountPoint,
            DokanDiskOperations operations,
            IDiskOperationsService remoteDiskService,
            DiskVolumeInfoDto volumeInfo,
            DriveLetterManager driveLetterManager)
        {
            Client = client ?? throw new ArgumentNullException(nameof(client));
            DriveLetter = driveLetter;
            MountPoint = mountPoint;
            Operations = operations ?? throw new ArgumentNullException(nameof(operations));
            RemoteDiskService = remoteDiskService ?? throw new ArgumentNullException(nameof(remoteDiskService));
            VolumeInfo = volumeInfo ?? throw new ArgumentNullException(nameof(volumeInfo));
            _driveLetterManager = driveLetterManager ?? throw new ArgumentNullException(nameof(driveLetterManager));
            MountedTime = DateTime.UtcNow;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                DokanInstance?.Dispose();
            }
            catch
            {
                // 忽略卸载时的异常
            }

            _driveLetterManager.Release(DriveLetter);
        }
    }
}
