using System.Collections.Generic;

namespace ApeFree.DonateDisk.Slave
{
    /// <summary>
    /// Slave 节点运行配置项
    /// </summary>
    public class DiskSlaveOptions
    {
        /// <summary>
        /// 目标 Master 端主机 IP 或域名（仅用于标准 TCP 模式）
        /// </summary>
        public string TargetHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// 目标 Master 端监听端口（仅用于标准 TCP 模式）
        /// </summary>
        public int TargetPort { get; set; } = 9821;

        /// <summary>
        /// 本地待共享给 Master 的根目录物理路径
        /// </summary>
        public string LocalDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 虚拟磁盘卷标名称（缺省自动根据目录名生成）
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
        /// P2P 引导与中继服务端地址（例如 1.2.3.4:4001 或 /ip4/1.2.3.4/tcp/4001/p2p/QmServer...）
        /// </summary>
        public string? P2PServerAddress { get; set; }

        /// <summary>
        /// 目标 Master 端的 PeerId（在 P2P 模式下，与 P2PServerAddress 配合进行中继寻址）
        /// </summary>
        public string? TargetPeerId { get; set; }

        /// <summary>
        /// 远程目标节点的完整 Multiaddr 地址（例如: /ip4/192.168.1.100/tcp/4001/p2p/12D3KooW...，若指定则优先使用）
        /// </summary>
        public string? P2PRemoteAddress { get; set; }

        /// <summary>
        /// 本地 P2P 监听端口（默认 0，系统随机分配可用端口）
        /// </summary>
        public int P2PPort { get; set; } = 0;

        /// <summary>
        /// 本地节点的身份私钥种子（字符串或十六进制，用于使 Slave 的 PeerId 跨重启保持固定）
        /// </summary>
        public string? IdentitySeed { get; set; }

        /// <summary>
        /// 是否启用 Relay 中继以支持 NAT 穿透（P2P 模式下默认开启）
        /// </summary>
        public bool EnableRelay { get; set; } = true;

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
