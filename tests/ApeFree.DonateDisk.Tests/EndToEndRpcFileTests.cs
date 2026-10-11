using ApeFree.ApeRpc;
using ApeFree.ApeRpc.BytesIO;
using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Contracts;
using ApeFree.DonateDisk.Master;
using ApeFree.DonateDisk.Slave;
using DokanNet;
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
            var masterTerminal = new RpcTerminal(new TerminalInfo { Name = "MasterNode" }) { InvokeTimeoutDuration = 10000 };
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

            // 等待 RPC 初始化握手完成并获取代理
            await Task.Delay(400);

            // 3. Master 端通过连接上的 Client 信道获取 Slave 暴露的磁盘服务代理
            var proxy = masterAddon.QueryService<IDiskOperationsService>(nameof(DiskOperationsService), connectedClient);
            Assert.NotNull(proxy);

            // 4. 测试 GetVolumeInfo（带容错重试以适应高并发测试环境）
            DiskVolumeInfoDto? volumeInfo = null;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    volumeInfo = proxy.GetVolumeInfo();
                    if (volumeInfo != null) break;
                }
                catch
                {
                    await Task.Delay(300);
                }
            }
            Assert.NotNull(volumeInfo);
            Assert.Equal("RemoteDonatedDisk", volumeInfo.VolumeLabel);

            // 5. 测试通过 RPC 创建文件并分块写入
            const string testPath = @"\greeting.txt";
            var contentStr = "Hello from Master over ApeRpc! 这是通过全双工 RPC 远程写入 Slave 的数据。";
            var writeBytes = Encoding.UTF8.GetBytes(contentStr);

            var openResult = proxy.OpenFile(testPath, (int)System.IO.FileAccess.ReadWrite, (int)FileShare.ReadWrite, (int)FileMode.Create, 0, 0);
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

        [Fact]
        public async Task FullDuplexRpc_DokanDiskOperations_OverRpc_WorksEndToEnd()
        {
            int port = GetAvailablePort();
            var masterTerminal = new RpcTerminal(new TerminalInfo { Name = "MasterNode2" }) { InvokeTimeoutDuration = 10000 };
            var masterAddon = new BytesRpcTerminalAddon(masterTerminal);
            var tcpServer = new STTcpServer { Host = "127.0.0.1", Port = port };
            masterAddon.BindServer(tcpServer);

            var clientConnectedTcs = new TaskCompletionSource<STTcpClient>();
            tcpServer.ClientConnected += (s, e) => clientConnectedTcs.TrySetResult(e.Client);
            await tcpServer.StartAsync();

            var slaveOptions = new DiskSlaveOptions
            {
                TargetHost = "127.0.0.1",
                TargetPort = port,
                LocalDirectory = _tempSharedDir,
                VolumeLabel = "RemoteE2E"
            };

            using var slaveNode = new DiskSlaveNode(slaveOptions);
            var connected = await slaveNode.StartAsync();
            Assert.True(connected);

            var connectedClient = await clientConnectedTcs.Task;
            await Task.Delay(400);

            var proxy = masterAddon.QueryService<IDiskOperationsService>(nameof(DiskOperationsService), connectedClient);
            Assert.NotNull(proxy);

            // 确保 RPC 双向通道完全建立
            DiskVolumeInfoDto? volCheck = null;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    volCheck = proxy.GetVolumeInfo();
                    if (volCheck != null) break;
                }
                catch
                {
                    await Task.Delay(300);
                }
            }
            Assert.NotNull(volCheck);

            using var dokanOps = new DokanDiskOperations(proxy, defaultVolumeLabel: "RemoteE2E");

            // 1. Create file through DokanDiskOperations
            var info = new MockDokanFileInfo();
            var createRes = dokanOps.CreateFile(@"\e2e_dokan.txt", DokanNet.FileAccess.WriteData | DokanNet.FileAccess.ReadData, FileShare.ReadWrite, FileMode.CreateNew, 0, 0, info);
            Assert.Equal(DokanResult.Success, createRes);
            Assert.NotNull(info.Context);

            // 2. Write file
            var data = Encoding.UTF8.GetBytes("Data written through Dokan over RPC!");
            var writeRes = dokanOps.WriteFile(@"\e2e_dokan.txt", data, out int written, 0, info);
            Assert.Equal(DokanResult.Success, writeRes);
            Assert.Equal(data.Length, written);

            // 3. Read file
            var readBuf = new byte[data.Length];
            var readRes = dokanOps.ReadFile(@"\e2e_dokan.txt", readBuf, out int readCount, 0, info);
            Assert.Equal(DokanResult.Success, readRes);
            Assert.Equal(data.Length, readCount);
            Assert.Equal(data, readBuf);

            dokanOps.CloseFile(@"\e2e_dokan.txt", info);

            // 4. Verify physical file on Slave
            var physicalPath = Path.Combine(_tempSharedDir, "e2e_dokan.txt");
            Assert.True(File.Exists(physicalPath));

            // 5. Move/Rename file through Dokan
            var moveRes = dokanOps.MoveFile(@"\e2e_dokan.txt", @"\e2e_dokan_renamed.txt", replace: false, info);
            Assert.Equal(DokanResult.Success, moveRes);
            Assert.False(File.Exists(physicalPath));
            Assert.True(File.Exists(Path.Combine(_tempSharedDir, "e2e_dokan_renamed.txt")));

            // 6. Delete file through Dokan Cleanup
            var openForDel = dokanOps.CreateFile(@"\e2e_dokan_renamed.txt", DokanNet.FileAccess.Delete, FileShare.ReadWrite | FileShare.Delete, FileMode.Open, 0, 0, info);
            Assert.Equal(DokanResult.Success, openForDel);
            dokanOps.DeleteFile(@"\e2e_dokan_renamed.txt", info);
            info.DeletePending = true;
            dokanOps.Cleanup(@"\e2e_dokan_renamed.txt", info);
            dokanOps.CloseFile(@"\e2e_dokan_renamed.txt", info);

            Assert.False(File.Exists(Path.Combine(_tempSharedDir, "e2e_dokan_renamed.txt")));

            slaveNode.Stop();
            await tcpServer.CloseAsync();
            masterAddon.Dispose();
        }
    }
}
