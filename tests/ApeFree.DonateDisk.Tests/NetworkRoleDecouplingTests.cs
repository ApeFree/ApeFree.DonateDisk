using ApeFree.ApeRpc;
using ApeFree.ApeRpc.BytesIO;
using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Contracts;
using ApeFree.DonateDisk.Slave;
using ApeFree.DonateDisk.WinCli.Common;
using ApeFree.PyramidMenu.Options;
using ApeFree.PyramidMenu.Scenes;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using STTcpClient = STTech.BytesIO.Tcp.TcpClient;

namespace ApeFree.DonateDisk.Tests
{
    /// <summary>
    /// 网络传输角色与业务角色解耦端到端测试：
    /// 验证 Slave 作为 Server 被动监听、外部 Client 主动拨号连入并调用全双工 RPC 的可行性与稳定性。
    /// </summary>
    public class NetworkRoleDecouplingTests : IDisposable
    {
        private readonly string _tempSharedDir;
        private readonly int _testPort;

        public NetworkRoleDecouplingTests()
        {
            _tempSharedDir = Path.Combine(Path.GetTempPath(), "DonateDisk_Decouple_" + Guid.NewGuid().ToString("N"));
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
        public async Task SlaveAsServer_MasterAsClient_CanConnectAndQueryDiskService()
        {
            // 准备共享文件
            var testFilePath = Path.Combine(_tempSharedDir, "welcome.txt");
            await File.WriteAllTextAsync(testFilePath, "Hello From Decoupled Slave Server!", new UTF8Encoding(false));

            // 1. 启动 Slave 服务端监听模式 (Listen 模式)
            var slaveOptions = new DiskSlaveOptions
            {
                TransportMode = NetworkTransportMode.Listen,
                ListenHost = "127.0.0.1",
                ListenPort = _testPort,
                LocalDirectory = _tempSharedDir,
                VolumeLabel = "TestDecoupledVolume"
            };

            using var slaveNode = new DiskSlaveNode(slaveOptions);
            var started = await slaveNode.StartAsync();
            Assert.True(started);
            Assert.True(slaveNode.IsListening);

            // 2. 作为客户端主动连入 Slave 服务端
            using var clientTcp = new STTcpClient { Host = "127.0.0.1", Port = _testPort };
            var clientTerminal = new RpcTerminal(new TerminalInfo { Name = "ActiveMasterClient" }) { InvokeTimeoutDuration = 10000 };
            using var clientAddon = new BytesRpcTerminalAddon(clientTerminal, clientTcp);

            var connResult = await clientTcp.ConnectAsync();
            Assert.True(connResult.IsSuccess);

            // 等待 RPC 注册同步完成
            await Task.Delay(300);
            Assert.True(slaveNode.ConnectedClientCount >= 1);

            // 3. 客户端调用 QueryService 消费 Slave 提供的磁盘操作服务
            var proxy = clientAddon.QueryService<IDiskOperationsService>(slaveOptions.ServiceName, clientTcp);
            Assert.NotNull(proxy);

            // 验证卷信息
            var volumeInfo = proxy.GetVolumeInfo();
            Assert.Equal("TestDecoupledVolume", volumeInfo.VolumeLabel);

            // 验证文件查找
            var files = proxy.FindFiles(string.Empty);
            Assert.Contains(files, f => f.Name == "welcome.txt" && !f.IsDirectory);

            // 验证打开与分块读取文件
            var openResult = proxy.OpenFile("welcome.txt", (int)FileAccess.Read, (int)FileShare.ReadWrite, (int)FileMode.Open, 0, 0);
            Assert.True(openResult.HandleId > 0);
            Assert.True(openResult.Length > 0);

            var readBytes = proxy.ReadBlock(openResult.HandleId, 0, (int)openResult.Length);
            var readContent = new UTF8Encoding(false).GetString(readBytes);
            Assert.Equal("Hello From Decoupled Slave Server!", readContent);

            proxy.CloseFile(openResult.HandleId);

            // 4. 断开连接
            clientTcp.Disconnect();
            await Task.Delay(300);
            Assert.Equal(0, slaveNode.ConnectedClientCount);
        }

        [Fact]
        public async Task SlaveAsServer_MultipleClientsConcurrent_IsolatedHandles()
        {
            var slaveOptions = new DiskSlaveOptions
            {
                TransportMode = NetworkTransportMode.Listen,
                ListenHost = "127.0.0.1",
                ListenPort = _testPort,
                LocalDirectory = _tempSharedDir
            };

            using var slaveNode = new DiskSlaveNode(slaveOptions);
            await slaveNode.StartAsync();

            // Client 1
            using var tcp1 = new STTcpClient { Host = "127.0.0.1", Port = _testPort };
            var term1 = new RpcTerminal(new TerminalInfo { Name = "Client1" }) { InvokeTimeoutDuration = 10000 };
            using var addon1 = new BytesRpcTerminalAddon(term1, tcp1);
            await tcp1.ConnectAsync();

            // Client 2
            using var tcp2 = new STTcpClient { Host = "127.0.0.1", Port = _testPort };
            var term2 = new RpcTerminal(new TerminalInfo { Name = "Client2" }) { InvokeTimeoutDuration = 10000 };
            using var addon2 = new BytesRpcTerminalAddon(term2, tcp2);
            await tcp2.ConnectAsync();

            await Task.Delay(300);
            Assert.Equal(2, slaveNode.ConnectedClientCount);

            var proxy1 = addon1.QueryService<IDiskOperationsService>(slaveOptions.ServiceName, tcp1);
            var proxy2 = addon2.QueryService<IDiskOperationsService>(slaveOptions.ServiceName, tcp2);

            // Client 1 创建并写入 file1.txt
            var open1 = proxy1.OpenFile("file1.txt", (int)FileAccess.ReadWrite, (int)FileShare.ReadWrite, (int)FileMode.Create, 0, 0);
            var data1 = new UTF8Encoding(false).GetBytes("Client 1 Data");
            proxy1.WriteBlock(open1.HandleId, 0, data1);
            proxy1.Flush(open1.HandleId);

            // Client 2 创建并写入 file2.txt
            var open2 = proxy2.OpenFile("file2.txt", (int)FileAccess.ReadWrite, (int)FileShare.ReadWrite, (int)FileMode.Create, 0, 0);
            var data2 = new UTF8Encoding(false).GetBytes("Client 2 Data");
            proxy2.WriteBlock(open2.HandleId, 0, data2);
            proxy2.Flush(open2.HandleId);

            // 断开 Client 1
            tcp1.Disconnect();
            await Task.Delay(300);
            Assert.Equal(1, slaveNode.ConnectedClientCount);

            // Client 2 依然可以正常读写自己打开的文件
            var read2 = proxy2.ReadBlock(open2.HandleId, 0, data2.Length);
            Assert.Equal("Client 2 Data", new UTF8Encoding(false).GetString(read2));

            proxy2.CloseFile(open2.HandleId);
            tcp2.Disconnect();
        }

        [Fact]
        public void WizardRootMenu_Input4_ExitsSuccessfully()
        {
            var rootMenu = new DonateDiskMenuWizard.WizardRootMenu();
            var scene = new Scene(rootMenu);
            var result = scene.Input(new Packet("4"));
            Assert.True(result.IsSuccessful);
            Assert.True(rootMenu.IsFinished);
        }
    }
}
