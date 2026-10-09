using ApeFree.ApeRpc;
using ApeFree.ApeRpc.BytesIO;
using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Contracts;
using ApeFree.DonateDisk.Slave;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using STTcpClient = STTech.BytesIO.Tcp.TcpClient;
using STTcpServer = STTech.BytesIO.Tcp.TcpServer;

namespace ApeFree.DonateDisk.Tests
{
    public class EndToEndRpcFileTests : IDisposable
    {
        private readonly string _tempSharedDir;
        private readonly int _testPort;

        public EndToEndRpcFileTests()
        {
            _tempSharedDir = Path.Combine(Path.GetTempPath(), "DonateDisk_E2E_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempSharedDir);
            _testPort = GetAvailablePort();
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempSharedDir))
                {
                    Directory.Delete(_tempSharedDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public async Task FullDuplexRpc_SlaveDonates_MasterCanOperateRemoteFiles()
        {
            // 1. 初始化 Master 端服务监听
            var masterTerminal = new RpcTerminal(new TerminalInfo { Name = "MasterNode" });
            var masterAddon = new BytesRpcTerminalAddon(masterTerminal);
            var tcpServer = new STTcpServer { Host = "127.0.0.1", Port = _testPort };
            masterAddon.BindServer(tcpServer);

            var clientConnectedTcs = new TaskCompletionSource<STTcpClient>();
            tcpServer.ClientConnected += (s, e) =>
            {
                clientConnectedTcs.TrySetResult(e.Client);
            };

            await tcpServer.StartAsync();

            // 2. 初始化 Slave 端
            var slaveOptions = new DiskSlaveOptions
            {
                TargetHost = "127.0.0.1",
                TargetPort = _testPort,
                LocalDirectory = _tempSharedDir,
                VolumeLabel = "RemoteDonatedDisk"
            };

            using var slaveNode = new DiskSlaveNode(slaveOptions);
            var connected = await slaveNode.StartAsync();
            Assert.True(connected, "Slave 连接 Master 应当成功");

            // 等待 Master 端捕获到该 Client
            var connectedClient = await clientConnectedTcs.Task;
            Assert.NotNull(connectedClient);

            // 等待 RPC 初始化握手完成
            await Task.Delay(200);

            // 3. Master 端通过连接上的 Client 信道获取 Slave 暴露的磁盘服务代理
            var proxy = masterAddon.QueryService<IDiskOperationsService>(nameof(DiskOperationsService), connectedClient);
            Assert.NotNull(proxy);

            // 4. 测试 GetVolumeInfo
            var volumeInfo = proxy.GetVolumeInfo();
            Assert.Equal("RemoteDonatedDisk", volumeInfo.VolumeLabel);

            // 5. 测试通过 RPC 创建文件并分块写入
            const string testPath = @"\greeting.txt";
            var contentStr = "Hello from Master over ApeRpc! 这是通过全双工 RPC 远程写入 Slave 的数据。";
            var writeBytes = Encoding.UTF8.GetBytes(contentStr);

            var openResult = proxy.OpenFile(testPath, (int)FileAccess.ReadWrite, (int)FileShare.ReadWrite, (int)FileMode.Create, 0, 0);
            Assert.Equal(0, openResult.NtStatusCode);
            Assert.True(openResult.HandleId > 0);

            int written = proxy.WriteBlock(openResult.HandleId, 0, writeBytes);
            Assert.Equal(writeBytes.Length, written);

            // 6. 测试通过 RPC 读回数据
            var readBytes = proxy.ReadBlock(openResult.HandleId, 0, writeBytes.Length);
            Assert.Equal(writeBytes, readBytes);
            Assert.Equal(contentStr, Encoding.UTF8.GetString(readBytes));

            // 关闭句柄
            proxy.CloseFile(openResult.HandleId);

            // 7. 验证物理文件确已写入 Slave 磁盘目录
            var physicalPath = Path.Combine(_tempSharedDir, "greeting.txt");
            Assert.True(File.Exists(physicalPath));
            var physicalContent = File.ReadAllText(physicalPath, Encoding.UTF8);
            Assert.Equal(contentStr, physicalContent);

            // 8. 测试目录遍历 FindFiles
            var files = proxy.FindFiles(@"\");
            Assert.Single(files);
            Assert.Equal("greeting.txt", files[0].Name);
            Assert.Equal(writeBytes.Length, files[0].Length);

            // 清理
            slaveNode.Stop();
            await tcpServer.CloseAsync();
            masterAddon.Dispose();
        }
    }
}
