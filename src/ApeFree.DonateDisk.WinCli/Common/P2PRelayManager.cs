using STTech.BytesIO.Core;
using STTech.BytesIO.P2P;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace ApeFree.DonateDisk.Common
{
    /// <summary>
    /// P2P 引导与中继服务端对等节点中继管理器。
    /// <para>动态管理接入的对等节点生命周期，实现全双工字节流安全中继转发，</para>
    /// <para>彻底避免因节点重连或断开导致的闭包悬挂、内存泄漏与死连接转发问题。</para>
    /// </summary>
    public class P2PRelayManager : IDisposable
    {
        private readonly object _lock = new object();
        private readonly Dictionary<PeerClient, EventHandler<DataReceivedEventArgs>> _clients = new Dictionary<PeerClient, EventHandler<DataReceivedEventArgs>>();
        private bool _isDisposed;

        /// <summary>
        /// 当中继通道发生变更（节点加入或离开）时触发
        /// </summary>
        public event EventHandler<int>? ActivePeerCountChanged;

        /// <summary>
        /// 获取当前活动的中继对等节点数量
        /// </summary>
        public int ActivePeerCount
        {
            get
            {
                lock (_lock)
                {
                    return _clients.Count;
                }
            }
        }

        /// <summary>
        /// 注册一个接入的对等节点并挂载中继转发监听
        /// </summary>
        /// <param name="client">对等客户端</param>
        public void RegisterClient(PeerClient client)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            lock (_lock)
            {
                if (_isDisposed) return;

                // 若之前已注册过同实例，先安全注销旧监听
                if (_clients.TryGetValue(client, out var existingHandler))
                {
                    client.OnDataReceived -= existingHandler;
                    _clients.Remove(client);
                }

                // 创建无闭包引用的动态转发处理器
                EventHandler<DataReceivedEventArgs> handler = (sender, args) =>
                {
                    if (args?.Data == null || args.Data.Memory.Length == 0) return;
                    var payload = args.Data.Memory.ToArray();

                    List<PeerClient> targets;
                    lock (_lock)
                    {
                        if (_isDisposed) return;
                        targets = _clients.Keys.Where(c => c != client && c.IsConnected).ToList();
                    }

                    foreach (var target in targets)
                    {
                        try
                        {
                            target.Send(payload);
                        }
                        catch
                        {
                            // 忽略单个节点发送异常
                        }
                    }
                };

                client.OnDataReceived += handler;
                _clients[client] = handler;
            }

            ActivePeerCountChanged?.Invoke(this, ActivePeerCount);
        }

        /// <summary>
        /// 注销断开的对等节点并安全解除中继转发监听
        /// </summary>
        /// <param name="client">对等客户端</param>
        public void UnregisterClient(PeerClient client)
        {
            if (client == null) return;

            lock (_lock)
            {
                if (_clients.TryGetValue(client, out var handler))
                {
                    try
                    {
                        client.OnDataReceived -= handler;
                    }
                    catch { }
                    _clients.Remove(client);
                }
            }

            ActivePeerCountChanged?.Invoke(this, ActivePeerCount);
        }

        /// <summary>
        /// 释放中继管理器并解绑所有事件
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            lock (_lock)
            {
                foreach (var kv in _clients)
                {
                    try
                    {
                        kv.Key.OnDataReceived -= kv.Value;
                    }
                    catch { }
                }
                _clients.Clear();
            }

            ActivePeerCountChanged?.Invoke(this, 0);
        }
    }
}
