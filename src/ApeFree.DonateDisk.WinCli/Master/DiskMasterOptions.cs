using System.Collections.Generic;

namespace ApeFree.DonateDisk.Master
{
    /// <summary>
    /// Master 节点运行配置项
    /// </summary>
    public class DiskMasterOptions
    {
        /// <summary>
        /// 本地监听绑定的主机 IP 或域名（默认 0.0.0.0 监听所有网卡）
        /// </summary>
        public string ListenHost { get; set; } = "0.0.0.0";

        /// <summary>
        /// 本地监听端口（默认 9821）
        /// </summary>
        public int ListenPort { get; set; } = 9821;

        /// <summary>
        /// 首选分配的虚拟磁盘盘符字符（例如 'Z'）
        /// </summary>
        public char? PreferredDriveLetter { get; set; } = 'Z';

        /// <summary>
        /// 是否挂载为可移动磁盘（RemovableDrive），提高 Windows 资源管理器即插即拔兼容性
        /// </summary>
        public bool MountAsRemovable { get; set; } = true;

        /// <summary>
        /// 远程 RPC 服务契约名称（默认与 Slave 注册的 DiskOperationsService 一致）
        /// </summary>
        public string ServiceName { get; set; } = "DiskOperationsService";

        /// <summary>
        /// 是否启用 P2P (Libp2p) 监听模式
        /// </summary>
        public bool UseP2P { get; set; }

        /// <summary>
        /// P2P 监听端口（默认 4001）
        /// </summary>
        public int P2PPort { get; set; } = 4001;

        /// <summary>
        /// 是否启用 Relay 中继以支持 NAT 穿透
        /// </summary>
        public bool EnableRelay { get; set; }

        /// <summary>
        /// 是否启用 Kademlia DHT 节点发现
        /// </summary>
        public bool EnableDht { get; set; }

        /// <summary>
        /// 是否启用 mDNS 局域网节点发现
        /// </summary>
        public bool EnableMdns { get; set; }

        /// <summary>
        /// 是否启用 QUIC 传输协议
        /// </summary>
        public bool EnableQuic { get; set; }

        /// <summary>
        /// 引导节点 Multiaddr 列表
        /// </summary>
        public List<string> BootstrapPeers { get; set; } = new List<string>();
    }
}
