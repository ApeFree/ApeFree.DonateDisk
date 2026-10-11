using ApeFree.ApeRpc;
using ApeFree.ApeRpc.BytesIO;
using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Contracts;
using STTech.BytesIO.Core;
using STTech.BytesIO.P2P;
using STTech.BytesIO.Tcp;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ApeFree.DonateDisk.Slave
{
    /// <summary>
    /// Slave 节点控制宿主。
    /// 负责将本地指定物理目录包装为虚拟磁盘服务，主动拨号连接 Master 端并发布 ApeRpc 服务。
    /// 支持标准 TCP 直连以及 P2P (Libp2p) 点对点去中心化传输。
    /// </summary>
    public class DiskSlaveNode : IDisposable
    {
        private readonly DiskSlaveOptions _options;
        private BytesClient? _client;
        private RpcTerminal? _terminal;
        private BytesRpcTerminalAddon? _addon;
        private DiskOperationsService? _diskService;
        private bool _isDisposed;

        /// <summary>
        /// 当与 Master 端建立物理连接成功时触发
        /// </summary>
        public event EventHandler? Connected;

        /// <summary>
        /// 当与 Master 端的连接中断时触发
        /// </summary>
        public event EventHandler? Disconnected;

        /// <summary>
        /// 当本地被分享目录内的文件发生创建、修改、重命名或删除变动时触发
        /// </summary>
        public event EventHandler<FileChangeEventDto>? FileChanged;

        /// <summary>
        /// 获取配置项
        /// </summary>
        public DiskSlaveOptions Options => _options;

        /// <summary>
        /// 获取当前磁盘服务实例
        /// </summary>
        public DiskOperationsService? DiskService => _diskService;

        /// <summary>
        /// 当前是否已连接
        /// </summary>
        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>
        /// 最近一次连接尝试的结果详情
        /// </summary>
        public ConnectResult? LastConnectResult { get; private set; }

        /// <summary>
        /// 若在 P2P 模式下，获取当前本地节点的 PeerId
        /// </summary>
        public string? LocalPeerId => (_client as PeerClient)?.LocalPeerId;

        /// <summary>
        /// 初始化 Slave 节点
        /// </summary>
        /// <param name="options">运行配置项</param>
        public DiskSlaveNode(DiskSlaveOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(_options.LocalDirectory))
            {
                throw new ArgumentException("待分享的本地目录 LocalDirectory 不能为空。", nameof(options));
            }
        }

        /// <summary>
        /// 启动 Slave 节点并主动连接目标 Master 端
        /// </summary>
        /// <param name="cancellationToken">取消标记</param>
        /// <returns>连接并发布服务是否成功</returns>
        public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(DiskSlaveNode));

            // 1. 初始化路径沙箱与磁盘服务
            var resolver = new SafePathResolver(_options.LocalDirectory);
            var label = string.IsNullOrWhiteSpace(_options.VolumeLabel)
                ? new DirectoryInfo(resolver.RootDirectory).Name
                : _options.VolumeLabel;

            _diskService = new DiskOperationsService(resolver, label, _options.IsReadOnly);
            _diskService.FileChanged += (s, e) =>
            {
                FileChanged?.Invoke(this, e);
            };

            // 2. 初始化 RPC 终端与网络客户端
            var terminalInfo = new TerminalInfo
            {
                Name = $"SlaveNode-{Environment.MachineName}-{Guid.NewGuid().ToString("N").Substring(0, 6)}"
            };
            _terminal = new RpcTerminal(terminalInfo);

            if (_options.UseP2P)
            {
                var peerClient = new PeerClient();
                peerClient.Options.EnableRelay = _options.EnableRelay;
                peerClient.Options.EnableDht = _options.EnableDht;
                peerClient.Options.EnableMdns = _options.EnableMdns;
                peerClient.Options.EnableQuic = _options.EnableQuic;

                if (!string.IsNullOrWhiteSpace(_options.IdentitySeed))
                {
                    peerClient.Options.IdentitySeed = P2PAddressHelper.ParseSeed(_options.IdentitySeed);
                }

                if (string.IsNullOrWhiteSpace(_options.TargetPeerId) && !string.IsNullOrWhiteSpace(_options.MasterSeed))
                {
                    _options.TargetPeerId = P2PAddressHelper.GetPeerIdFromSeed(_options.MasterSeed);
                }

                if (!string.IsNullOrWhiteSpace(_options.P2PServerAddress))
                {
                    var resolvedServer = P2PAddressHelper.ResolveServerMultiaddr(_options.P2PServerAddress, _options.ServerPeerId, _options.ServerSeed);
                    peerClient.Options.BootstrapPeers.Add(resolvedServer);
                }

                if (_options.BootstrapPeers?.Count > 0)
                {
                    foreach (var bp in _options.BootstrapPeers)
                    {
                        if (!peerClient.Options.BootstrapPeers.Contains(bp))
                        {
                            peerClient.Options.BootstrapPeers.Add(bp);
                        }
                    }
                }

                // 确定连接远端节点的 Multiaddr 地址
                string remoteAddr;
                if (!string.IsNullOrWhiteSpace(_options.P2PRemoteAddress))
                {
                    remoteAddr = _options.P2PRemoteAddress;
                }
                else if (!string.IsNullOrWhiteSpace(_options.P2PServerAddress))
                {
                    remoteAddr = P2PAddressHelper.ResolveServerMultiaddr(_options.P2PServerAddress, _options.ServerPeerId, _options.ServerSeed);
                }
                else if (!string.IsNullOrWhiteSpace(_options.TargetPeerId))
                {
                    remoteAddr = $"/p2p/{_options.TargetPeerId}";
                }
                else
                {
                    var host = string.IsNullOrWhiteSpace(_options.TargetHost) ? "127.0.0.1" : _options.TargetHost;
                    var port = _options.TargetPort > 0 ? _options.TargetPort : 4001;
                    remoteAddr = P2PAddressHelper.ToMultiaddr($"{host}:{port}");
                }

                peerClient.RemoteAddress = remoteAddr;
                _client = peerClient;
            }
            else
            {
                _client = new TcpClient
                {
                    Host = _options.TargetHost,
                    Port = _options.TargetPort
                };
            }

            // 注册断开连接事件监听，及时释放本地文件句柄
            _client.OnDisconnected += (s, e) =>
            {
                _diskService?.DisposeAllSessions();
                Disconnected?.Invoke(this, EventArgs.Empty);
            };

            _client.OnConnectedSuccessfully += (s, e) =>
            {
                Connected?.Invoke(this, EventArgs.Empty);
            };

            // 3. 挂载 BytesRpc 插件并绑定信道
            _addon = new BytesRpcTerminalAddon(_terminal, _client);

            // 4. 注册服务契约实现
            _terminal.RegisterService(_diskService);

            // 5. 连接 Master 端
            var connectResult = await _client.ConnectAsync(new ConnectArgument { Timeout = 25000, CancellationToken = cancellationToken });
            LastConnectResult = connectResult;

            if (connectResult.IsSuccess && _options.UseP2P && !string.IsNullOrWhiteSpace(_options.P2PServerAddress))
            {
                // 在 P2P 服务端中继模式下，向 Master 发送上线就绪通知以立即触发挂载
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    try
                    {
                        var masterService = _addon?.QueryService<IMasterNodeService>(nameof(IMasterNodeService), _client);
                        masterService?.NotifySlaveReady();
                    }
                    catch
                    {
                        // 若 Master 尚未启动或未就绪，Master 启动时其自身的 Watcher 会自动探测并完成挂载
                    }
                });
            }

            return connectResult.IsSuccess;
        }

        /// <summary>
        /// 停止 Slave 节点并断开连接
        /// </summary>
        public void Stop()
        {
            if (_client != null && _client.IsConnected)
            {
                _client.Disconnect();
            }

            _diskService?.DisposeAllSessions();
        }

        /// <summary>
        /// 释放 Slave 节点资源，断开网络连接并释放所有打开的文件流
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Stop();

            _client?.Dispose();
            _addon?.Dispose();
        }
    }
}
