using ApeFree.ApeRpc;
using ApeFree.ApeRpc.BytesIO;
using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Contracts;
using DokanNet;
using STTech.BytesIO.Core;
using STTech.BytesIO.P2P;
using STTech.BytesIO.Tcp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ApeFree.DonateDisk.Master
{
    /// <summary>
    /// Master 节点控制宿主。
    /// 开放 TCP 服务端监听或 P2P (Libp2p) 引导监听，在 Slave 节点主动连入后获取其磁盘服务代理，
    /// 并通过 Dokany 驱动自动分配可用盘符挂载为 Windows 原生虚拟磁盘。
    /// </summary>
    public class DiskMasterNode : IMasterNodeService, IDisposable
    {
        private readonly DiskMasterOptions _options;
        private readonly DriveLetterManager _driveLetterManager = new DriveLetterManager();
        private readonly ConcurrentDictionary<BytesClient, MountedSlaveSession> _sessions = new ConcurrentDictionary<BytesClient, MountedSlaveSession>();
        private readonly SemaphoreSlim _mountLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _p2pWatcherCts;
        private TcpServer? _tcpServer;
        private BootstrapServer? _bootstrapServer;
        private PeerClient? _p2pClient;
        private RpcTerminal? _terminal;
        private BytesRpcTerminalAddon? _addon;
        private bool _isDisposed;

        /// <summary>
        /// 当一个 Slave 成功挂载为本地磁盘盘符时触发
        /// </summary>
        public event EventHandler<MountedSlaveSession>? SlaveMounted;

        /// <summary>
        /// 当一个已挂载的 Slave 断开连接并成功卸载盘符时触发
        /// </summary>
        public event EventHandler<MountedSlaveSession>? SlaveUnmounted;

        /// <summary>
        /// 当 Slave 接入但在尝试挂载磁盘时发生异常时触发
        /// </summary>
        public event EventHandler<(BytesClient Client, Exception Exception)>? MountFailed;

        /// <summary>
        /// 当监听到已挂载的 Slave 端目录文件发生变动时触发
        /// </summary>
        public event EventHandler<(MountedSlaveSession Session, FileChangeEventDto Change)>? RemoteFileChanged;

        /// <summary>
        /// 获取配置项
        /// </summary>
        public DiskMasterOptions Options => _options;

        /// <summary>
        /// 获取盘符管理器
        /// </summary>
        public DriveLetterManager DriveLetterManager => _driveLetterManager;

        /// <summary>
        /// 获取当前所有处于活跃挂载状态的 Slave 磁盘会话列表
        /// </summary>
        public IReadOnlyCollection<MountedSlaveSession> MountedSessions => _sessions.Values.ToList();

        /// <summary>
        /// 服务端当前是否正在监听或处于已连接状态
        /// </summary>
        public bool IsListening => (_tcpServer != null && _tcpServer.IsListening) || (_bootstrapServer != null && _bootstrapServer.IsListening) || (_p2pClient != null && _p2pClient.IsConnected);

        /// <summary>
        /// 若在 P2P 模式下，获取当前本地节点的 PeerId
        /// </summary>
        public string? P2PLocalPeerId => _p2pClient?.LocalPeerId ?? _bootstrapServer?.LocalPeerId;

        /// <summary>
        /// 若在 P2P 模式下，获取当前本地节点的监听 Multiaddr 地址列表
        /// </summary>
        public IReadOnlyList<string>? P2PListenAddresses => _bootstrapServer?.ListenAddresses;

        /// <summary>
        /// 暴露给 RPC 终端的服务名称标识
        /// </summary>
        public string ServiceName => nameof(IMasterNodeService);

        /// <summary>
        /// 初始化 Master 节点
        /// </summary>
        /// <param name="options">运行配置项</param>
        public DiskMasterNode(DiskMasterOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// 启动 Master 节点并开始监听端口
        /// </summary>
        public async Task StartAsync()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(DiskMasterNode));

            // 1. 初始化终端
            var terminalInfo = new TerminalInfo
            {
                Name = $"MasterNode-{Environment.MachineName}"
            };
            _terminal = new RpcTerminal(terminalInfo);
            _terminal.InvokeTimeoutDuration = 2500;
            _addon = new BytesRpcTerminalAddon(_terminal);
            _terminal.RegisterService(this);

            if (_options.UseP2P)
            {
                if (!string.IsNullOrWhiteSpace(_options.P2PServerAddress))
                {
                    // 2a. 主动连接指定的公网 P2P 服务端 (p2p-server)
                    var peerClient = new PeerClient();
                    var resolvedServerAddr = P2PAddressHelper.ResolveServerMultiaddr(_options.P2PServerAddress, _options.ServerPeerId, _options.ServerSeed);
                    peerClient.RemoteAddress = resolvedServerAddr;
                    peerClient.Options.EnableRelay = _options.EnableRelay;
                    peerClient.Options.EnableDht = _options.EnableDht;
                    peerClient.Options.EnableMdns = _options.EnableMdns;
                    peerClient.Options.EnableQuic = _options.EnableQuic;

                    if (!string.IsNullOrWhiteSpace(_options.IdentitySeed))
                    {
                        peerClient.Options.IdentitySeed = P2PAddressHelper.ParseSeed(_options.IdentitySeed);
                    }

                    _p2pClient = peerClient;
                    _addon.BindClient(peerClient);

                    peerClient.OnConnectedSuccessfully += (s, e) =>
                    {
                        StartP2PSlaveWatcher(peerClient);
                    };

                    peerClient.OnDisconnected += (s, e) =>
                    {
                        _p2pWatcherCts?.Cancel();
                        HandleClientDisconnected(peerClient);
                    };

                    var connectResult = await peerClient.ConnectAsync(new STTech.BytesIO.Core.ConnectArgument { Timeout = 25000 }).ConfigureAwait(false);
                    if (!connectResult.IsSuccess)
                    {
                        var errDetail = connectResult.Exception != null ? $" ({connectResult.Exception.Message})" : string.Empty;
                        throw new InvalidOperationException($"无法连接至 P2P 服务端 ({_options.P2PServerAddress}): {connectResult.ErrorCode}{errDetail}");
                    }

                    // 连接成功后启动后台 Slave 探测与发现监听
                    StartP2PSlaveWatcher(peerClient);
                }
                else
                {
                    // 2b. 作为本地 P2P 服务端监听
                    _bootstrapServer = new BootstrapServer();
                    _bootstrapServer.Options.ListenAddress = _options.ListenHost;
                    _bootstrapServer.Options.TcpPort = _options.P2PPort > 0 ? _options.P2PPort : 4001;
                    _bootstrapServer.Options.EnableRelay = _options.EnableRelay;
                    _bootstrapServer.Options.EnableDht = _options.EnableDht;
                    _bootstrapServer.Options.EnableMdns = _options.EnableMdns;
                    _bootstrapServer.Options.EnableQuic = _options.EnableQuic;

                    if (!string.IsNullOrWhiteSpace(_options.IdentitySeed))
                    {
                        _bootstrapServer.Options.IdentitySeed = P2PAddressHelper.ParseSeed(_options.IdentitySeed);
                    }

                    if (_options.BootstrapPeers?.Count > 0)
                    {
                        foreach (var peer in _options.BootstrapPeers)
                        {
                            if (!_bootstrapServer.Options.BootstrapPeers.Contains(peer))
                            {
                                _bootstrapServer.Options.BootstrapPeers.Add(peer);
                            }
                        }
                    }

                    _bootstrapServer.PeerConnected += (s, e) =>
                    {
                        _addon.BindClient(e.Client);
                        HandleClientConnected(e.Client);
                    };

                    _bootstrapServer.PeerDisconnected += (s, e) =>
                    {
                        _addon.UnbindClient(e.Client);
                        HandleClientDisconnected(e.Client);
                    };

                    await _bootstrapServer.StartAsync().ConfigureAwait(false);
                }
            }
            else
            {
                // 2. 初始化标准 TCP 监听服务端
                _tcpServer = new TcpServer
                {
                    Host = _options.ListenHost,
                    Port = _options.ListenPort
                };

                _addon.BindServer(_tcpServer);

                _tcpServer.ClientConnected += (s, e) => HandleClientConnected(e.Client);
                _tcpServer.ClientDisconnected += (s, e) => HandleClientDisconnected(e.Client);

                await _tcpServer.StartAsync();
            }
        }

        /// <inheritdoc/>
        public void NotifySlaveReady()
        {
            if (_p2pClient != null && _p2pClient.IsConnected && !_sessions.ContainsKey(_p2pClient))
            {
                Task.Run(async () =>
                {
                    await TryMountSlaveAsync(_p2pClient).ConfigureAwait(false);
                });
            }
        }

        private void StartP2PSlaveWatcher(BytesClient client)
        {
            _p2pWatcherCts?.Cancel();
            _p2pWatcherCts = new CancellationTokenSource();
            var token = _p2pWatcherCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested && client.IsConnected && !_isDisposed)
                {
                    if (!_sessions.ContainsKey(client))
                    {
                        try
                        {
                            var mounted = await TryMountSlaveAsync(client, token).ConfigureAwait(false);
                            if (mounted)
                            {
                                break;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception)
                        {
                            // 探测未命中（远端 Slave 尚未就绪），继续轮询等待
                        }
                    }
                    else
                    {
                        break;
                    }

                    try
                    {
                        await Task.Delay(1500, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);
        }

        private async Task<bool> TryMountSlaveAsync(BytesClient client, CancellationToken cancellationToken = default)
        {
            if (_isDisposed || !client.IsConnected) return false;
            if (_sessions.ContainsKey(client)) return true;

            await _mountLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_sessions.ContainsKey(client)) return true;
                if (_addon == null || _terminal == null || !client.IsConnected) return false;

                // 1. 获取 Slave 端磁盘服务代理
                var proxy = _addon.QueryService<IDiskOperationsService>(_options.ServiceName, client);
                if (proxy == null) return false;

                // 2. 查询远端磁盘信息 (若对端尚未就绪或中继未打通，此处会超时抛出异常)
                DiskVolumeInfoDto? volInfo = null;
                try
                {
                    volInfo = proxy.GetVolumeInfo();
                }
                catch
                {
                    // 对端未就绪，本次探测未命中
                    return false;
                }

                if (volInfo == null) return false;

                // 3. 分配可用空闲盘符
                var letter = _driveLetterManager.Allocate(_options.PreferredDriveLetter);
                if (!letter.HasValue)
                {
                    throw new InvalidOperationException("系统没有可用的驱动器盘符（D-Z 均已被占用）。");
                }

                var mountPoint = DriveLetterHelper.FormatMountPoint(letter.Value, withTrailingSlash: true);
                var ops = new DokanDiskOperations(proxy, defaultVolumeLabel: volInfo.VolumeLabel);
                var session = new MountedSlaveSession(client, letter.Value, mountPoint, ops, proxy, volInfo, _driveLetterManager);

                // 4. 构建 DokanInstance 并挂载到 Windows
                var dokan = new Dokan(null!);
                var builder = new DokanInstanceBuilder(dokan)
                    .ConfigureOptions(opt =>
                    {
                        var dokanOption = _options.MountAsRemovable ? DokanOptions.RemovableDrive : DokanOptions.FixedDrive;
                        opt.Options = dokanOption | DokanOptions.MountManager;
                        opt.MountPoint = mountPoint;
                    });

                session.RemoteFileChanged += (s, change) =>
                {
                    RemoteFileChanged?.Invoke(this, (session, change));
                };

                session.DokanInstance = builder.Build(ops);
                _sessions[client] = session;

                SlaveMounted?.Invoke(this, session);
                return true;
            }
            catch (Exception ex)
            {
                MountFailed?.Invoke(this, (client, ex));
                throw;
            }
            finally
            {
                _mountLock.Release();
            }
        }

        private void HandleClientConnected(BytesClient client)
        {
            // 在后台异步处理 Slave 磁盘挂载流程，避免阻塞网络接受循环
            Task.Run(async () =>
            {
                try
                {
                    // 稍微缓冲以等待对端完成 Terminal 初始化与服务注册
                    await Task.Delay(350);
                    await TryMountSlaveAsync(client).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    MountFailed?.Invoke(this, (client, ex));
                }
            });
        }

        private void HandleClientDisconnected(BytesClient client)
        {
            if (_sessions.TryRemove(client, out var session))
            {
                session.Dispose();
                SlaveUnmounted?.Invoke(this, session);
            }
        }

        /// <summary>
        /// 停止 Master 节点，卸载所有挂载的盘符并关闭监听端口
        /// </summary>
        public async Task StopAsync()
        {
            _p2pWatcherCts?.Cancel();

            foreach (var kv in _sessions)
            {
                if (_sessions.TryRemove(kv.Key, out var session))
                {
                    session.Dispose();
                }
            }

            if (_tcpServer != null)
            {
                await _tcpServer.CloseAsync();
                _tcpServer = null;
            }

            if (_bootstrapServer != null)
            {
                await _bootstrapServer.CloseAsync();
                _bootstrapServer = null;
            }

            if (_p2pClient != null)
            {
                _p2pClient.Disconnect();
                _p2pClient.Dispose();
                _p2pClient = null;
            }
        }

        /// <summary>
        /// 释放 Master 节点资源，停止监听并卸载所有挂载盘符
        /// </summary>
        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            StopAsync().GetAwaiter().GetResult();
            _tcpServer?.Dispose();
            _bootstrapServer?.Dispose();
            _p2pClient?.Dispose();
            _addon?.Dispose();
        }
    }
}
