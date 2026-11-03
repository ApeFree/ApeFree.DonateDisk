namespace ApeFree.DonateDisk.Common
{
    /// <summary>
    /// 网络传输模式
    /// </summary>
    public enum NetworkTransportMode
    {
        /// <summary>
        /// 服务端被动监听端口，等待对端连入
        /// </summary>
        Listen,

        /// <summary>
        /// 客户端主动发起连接，拨号连接目标对端
        /// </summary>
        Connect,

        /// <summary>
        /// 基于 Libp2p 的去中心化中继穿透模式
        /// </summary>
        P2P
    }
}
