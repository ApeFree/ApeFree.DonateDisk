namespace ApeFree.DonateDisk.Slave
{
    /// <summary>
    /// Slave 节点运行配置项
    /// </summary>
    public class DiskSlaveOptions
    {
        /// <summary>
        /// 目标 Master 端主机 IP 或域名
        /// </summary>
        public string TargetHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// 目标 Master 端监听端口
        /// </summary>
        public int TargetPort { get; set; } = 9821;

        /// <summary>
        /// 本地待共享给 Master 的根目录物理路径
        /// </summary>
        public string LocalDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 虚拟磁盘卷标名称（缺省自动根据计算机名或目录名生成）
        /// </summary>
        public string? VolumeLabel { get; set; }

        /// <summary>
        /// 是否以只读模式捐赠分享（防止远端写入或删除）
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// 发布给 ApeRpc 的服务契约名称（默认使用 DiskOperationsService）
        /// </summary>
        public string ServiceName { get; set; } = nameof(DiskOperationsService);
    }
}
