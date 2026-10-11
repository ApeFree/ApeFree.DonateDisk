using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ApeFree.DonateDisk.Common
{
    /// <summary>
    /// P2P 网络地址与身份凭据转换工具类
    /// </summary>
    public static class P2PAddressHelper
    {
        /// <summary>
        /// 默认预设的 P2P 引导与中继服务端凭据种子（确保在未显式配置种子时，客户端与服务端拥有相同稳定的 PeerId）
        /// </summary>
        public const string DefaultServerSeed = "ApeFree.DonateDisk.P2PServer.DefaultSeed";

        /// <summary>
        /// 根据指定的种子字符串派生计算出唯一的 Libp2p PeerId
        /// </summary>
        /// <param name="seed">种子口令或十六进制私钥</param>
        /// <returns>Libp2p PeerId 字符串（形如 12D3KooW...）</returns>
        public static string? GetPeerIdFromSeed(string? seed)
        {
            var seedBytes = ParseSeed(seed);
            if (seedBytes == null) return null;
            return new Nethermind.Libp2p.Core.Identity(seedBytes).PeerId.ToString();
        }

        /// <summary>
        /// 解析 P2P 服务端 Multiaddr 地址，确保包含合法的 PeerId
        /// </summary>
        /// <param name="serverAddress">服务端地址（支持 127.0.0.1:9831, /ip4/..., 127.0.0.1:9831/p2p/...）</param>
        /// <param name="serverPeerId">指定的 PeerId（可选）</param>
        /// <param name="serverSeed">指定的服务端种子（可选）</param>
        /// <returns>包含 /p2p/ 的完整 Multiaddr</returns>
        public static string ResolveServerMultiaddr(string serverAddress, string? serverPeerId = null, string? serverSeed = null)
        {
            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                throw new ArgumentException("P2P 服务端地址不能为空", nameof(serverAddress));
            }

            // 1. 若地址已包含 /p2p/，直接转换并返回
            if (serverAddress.Contains("/p2p/"))
            {
                return ToMultiaddr(serverAddress);
            }

            // 2. 若显式指定了 serverPeerId
            if (!string.IsNullOrWhiteSpace(serverPeerId))
            {
                return ToMultiaddr(serverAddress, peerId: serverPeerId);
            }

            // 3. 若显式指定了 serverSeed
            if (!string.IsNullOrWhiteSpace(serverSeed))
            {
                var derivedPeerId = GetPeerIdFromSeed(serverSeed);
                return ToMultiaddr(serverAddress, peerId: derivedPeerId);
            }

            // 4. 缺省使用默认服务端种子推导稳定的 PeerId
            var defaultPeerId = GetPeerIdFromSeed(DefaultServerSeed);
            return ToMultiaddr(serverAddress, peerId: defaultPeerId);
        }

        /// <summary>
        /// 将常见的 IP/域名/端口格式转换为 Libp2p 标准 Multiaddr 格式
        /// </summary>
        /// <param name="address">输入地址（例如 127.0.0.1:9831, 127.0.0.1:9831/p2p/Qm..., /ip4/127.0.0.1/tcp/9831）</param>
        /// <param name="peerId">可选的目标 PeerId</param>
        /// <param name="defaultPort">若未指定端口时的缺省端口号</param>
        /// <returns>标准的 Multiaddr 字符串</returns>
        public static string ToMultiaddr(string address, string? peerId = null, int defaultPort = 9831)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("地址不能为空", nameof(address));
            }

            var trimmed = address.Trim();

            // 如果输入包含内嵌的 /p2p/
            string? embeddedPeerId = null;
            var p2pIndex = trimmed.IndexOf("/p2p/", StringComparison.OrdinalIgnoreCase);
            if (p2pIndex >= 0)
            {
                embeddedPeerId = trimmed.Substring(p2pIndex + 5).Trim();
                trimmed = trimmed.Substring(0, p2pIndex).TrimEnd('/');
            }

            var effectivePeerId = !string.IsNullOrWhiteSpace(peerId) ? peerId.Trim() : embeddedPeerId;
            if (effectivePeerId != null && effectivePeerId.StartsWith("/p2p/"))
            {
                effectivePeerId = effectivePeerId.Substring(5).Trim();
            }

            string baseAddr;
            if (trimmed.StartsWith("/"))
            {
                baseAddr = trimmed;
            }
            else
            {
                string host = trimmed;
                int port = defaultPort;

                // 处理可能包含端口的情况
                var lastColon = trimmed.LastIndexOf(':');
                if (lastColon > 0)
                {
                    var portPart = trimmed.Substring(lastColon + 1);
                    var hostPart = trimmed.Substring(0, lastColon).Trim('[', ']');
                    if (int.TryParse(portPart, out var p))
                    {
                        host = hostPart;
                        port = p;
                    }
                }

                if (IPAddress.TryParse(host, out var ip))
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        baseAddr = $"/ip6/{ip}/tcp/{port}";
                    }
                    else
                    {
                        baseAddr = $"/ip4/{ip}/tcp/{port}";
                    }
                }
                else
                {
                    baseAddr = $"/dns4/{host}/tcp/{port}";
                }
            }

            if (!string.IsNullOrWhiteSpace(effectivePeerId) && !baseAddr.Contains("/p2p/"))
            {
                return $"{baseAddr.TrimEnd('/')}/p2p/{effectivePeerId}";
            }

            return baseAddr;
        }

        /// <summary>
        /// 组合 P2P 服务端地址与目标节点 PeerId 构建 Libp2p Circuit Relay 中继地址
        /// </summary>
        /// <param name="serverAddress">P2P 服务端地址（支持 IP:Port 或 Multiaddr）</param>
        /// <param name="targetPeerId">目标节点的 PeerId</param>
        /// <returns>构建后的 Relay 中继 Multiaddr</returns>
        public static string BuildRelayAddress(string serverAddress, string targetPeerId)
        {
            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                throw new ArgumentException("P2P 服务端地址不能为空", nameof(serverAddress));
            }
            if (string.IsNullOrWhiteSpace(targetPeerId))
            {
                throw new ArgumentException("目标 PeerId 不能为空", nameof(targetPeerId));
            }

            var cleanTarget = targetPeerId.Trim();
            if (cleanTarget.StartsWith("/p2p/"))
            {
                cleanTarget = cleanTarget.Substring(5);
            }

            var baseServerAddr = ToMultiaddr(serverAddress).TrimEnd('/');

            if (baseServerAddr.EndsWith("/p2p-circuit"))
            {
                return $"{baseServerAddr}/p2p/{cleanTarget}";
            }

            if (baseServerAddr.Contains("/p2p-circuit/p2p/"))
            {
                return baseServerAddr;
            }

            return $"{baseServerAddr}/p2p-circuit/p2p/{cleanTarget}";
        }

        /// <summary>
        /// 解析用户指定的字符串/十六进制种子为标准的 32 字节 Ed25519 私钥种子
        /// </summary>
        /// <param name="seed">种子字符串（支持十六进制或任意自定义口令）</param>
        /// <returns>32 字节私钥种子</returns>
        public static byte[]? ParseSeed(string? seed)
        {
            if (string.IsNullOrWhiteSpace(seed)) return null;

            var trimmed = seed.Trim();

            // 若用户提供了 64 位十六进制字符串，尝试直接解析为 32 字节
            if (trimmed.Length == 64 && IsHexString(trimmed))
            {
                try
                {
                    var bytes = new byte[32];
                    for (int i = 0; i < 32; i++)
                    {
                        bytes[i] = Convert.ToByte(trimmed.Substring(i * 2, 2), 16);
                    }
                    return bytes;
                }
                catch { }
            }

            // 任意自定义口令通过 SHA-256 哈希计算出唯一的 32 字节私钥种子
            return SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        }

        private static bool IsHexString(string s)
        {
            foreach (var c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
