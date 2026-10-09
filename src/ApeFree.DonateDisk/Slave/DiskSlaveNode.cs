using ApeFree.ApeRpc;
using ApeFree.ApeRpc.BytesIO;
using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Contracts;
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
    /// </summary>
    public class DiskSlaveNode : IDisposable
    {
        private readonly DiskSlaveOptions _options;
        private TcpClient? _tcpClient;
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
        public bool IsConnected => _tcpClient != null && _tcpClient.IsConnected;

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

            _tcpClient = new TcpClient
            {
                Host = _options.TargetHost,
                Port = _options.TargetPort
            };

            // 注册断开连接事件监听，及时释放本地文件句柄
            _tcpClient.OnDisconnected += (s, e) =>
            {
                _diskService?.DisposeAllSessions();
                Disconnected?.Invoke(this, EventArgs.Empty);
            };

            _tcpClient.OnConnectedSuccessfully += (s, e) =>
            {
                Connected?.Invoke(this, EventArgs.Empty);
            };

            // 3. 挂载 BytesRpc 插件并绑定信道
            _addon = new BytesRpcTerminalAddon(_terminal, _tcpClient);

            // 4. 注册服务契约实现
            _terminal.RegisterService(_diskService);

            // 5. 连接 Master 端
            var connectResult = await _tcpClient.ConnectAsync();
            return connectResult.IsSuccess;
        }

        /// <summary>
        /// 停止 Slave 节点并断开连接
        /// </summary>
        public void Stop()
        {
            if (_tcpClient != null && _tcpClient.IsConnected)
            {
                _tcpClient.Disconnect();
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
            _tcpClient?.Dispose();
            _diskService?.Dispose();
            _addon?.Dispose();
        }
    }
}
