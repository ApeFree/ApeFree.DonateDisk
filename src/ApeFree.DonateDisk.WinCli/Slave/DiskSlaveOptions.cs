using System.Collections.Generic;

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

        /// <summary>
        /// 是否启用 P2P (Libp2p) 点对点传输模式
        /// </summary>
        public bool UseP2P { get; set; }

        /// <summary>
        /// 远程 P2P 节点的 Multiaddr 地址（例如: /ip4/192.168.1.100/tcp/4001/p2p/12D3KooW...）
        /// </summary>
        public string? P2PRemoteAddress { get; set; }

        /// <summary>
        /// 目标 P2P 节点的 PeerId（与 TargetHost/TargetPort 组合构建 Multiaddr 地址）
        /// </summary>
        public string? P2PPeerId { get; set; }

        /// <summary>
        /// P2P 端口号（默认为 4001）
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
