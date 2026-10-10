using System.Collections.Generic;

namespace ApeFree.DonateDisk.Master
{
    /// <summary>
    /// Master 节点运行配置项
    /// </summary>
    public class DiskMasterOptions
    {
        /// <summary>
        /// 本地监听绑定的主机 IP 或域名（默认 0.0.0.0 监听所有网卡，仅用于标准 TCP 模式）
        /// </summary>
        public string ListenHost { get; set; } = "0.0.0.0";

        /// <summary>
        /// 本地监听端口（默认 9821，仅用于标准 TCP 模式）
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
        /// 是否启用 P2P (Libp2p) 去中心化模式
        /// </summary>
        public bool UseP2P { get; set; }

        /// <summary>
        /// P2P 引导与中继服务端地址（例如 1.2.3.4:4001 或 /ip4/1.2.3.4/tcp/4001/p2p/QmServer...）
        /// </summary>
        public string? P2PServerAddress { get; set; }

        /// <summary>
        /// 本地 P2P 监听端口（默认 0，系统随机选择可用端口）
        /// </summary>
        public int P2PPort { get; set; } = 0;

        /// <summary>
        /// 本地节点的身份私钥种子（字符串或十六进制，用于使 Master 的 PeerId 跨重启保持固定）
        /// </summary>
        public string? IdentitySeed { get; set; }

        /// <summary>
        /// P2P 引导与中继服务端的 PeerId（可选，若未指定则通过 ServerSeed 或默认服务端种子自动推导）
        /// </summary>
        public string? ServerPeerId { get; set; }

        /// <summary>
        /// P2P 引导与中继服务端的凭据种子（可选，若服务端通过固定种子启动，可在此指定相同种子以自动匹配服务端 PeerId）
        /// </summary>
        public string? ServerSeed { get; set; }

        /// <summary>
        /// 目标 Slave 端节点的 PeerId（可选，若配置则主动寻址该 Slave）
        /// </summary>
        public string? TargetPeerId { get; set; }

        /// <summary>
        /// 是否启用 Relay 中继协议（Nethermind.Libp2p 尚未实现内建 Relay，默认 false）
        /// </summary>
        public bool EnableRelay { get; set; } = false;

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
