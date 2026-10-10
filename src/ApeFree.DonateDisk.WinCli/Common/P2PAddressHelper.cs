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
        /// 将常见的 IP/域名/端口格式转换为 Libp2p 标准 Multiaddr 格式
        /// </summary>
        /// <param name="address">输入地址（例如 1.2.3.4:4001, /ip4/1.2.3.4/tcp/4001）</param>
        /// <param name="defaultPort">若未指定端口时的缺省端口号</param>
        /// <returns>标准的 Multiaddr 字符串</returns>
        public static string ToMultiaddr(string address, int defaultPort = 4001)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("地址不能为空", nameof(address));
            }

            var trimmed = address.Trim();
            if (trimmed.StartsWith("/"))
            {
                return trimmed;
            }

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
                    return $"/ip6/{ip}/tcp/{port}";
                }
                return $"/ip4/{ip}/tcp/{port}";
            }

            return $"/dns4/{host}/tcp/{port}";
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
