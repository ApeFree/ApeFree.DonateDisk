using ApeFree.DonateDisk.Common;
using ApeFree.DonateDisk.Master;
using ApeFree.DonateDisk.Slave;
using System;
using System.IO;
using Xunit;

namespace ApeFree.DonateDisk.Tests
{
    public class P2POptionsTests : IDisposable
    {
        private readonly string _tempDir;

        public P2POptionsTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "ApeFree_P2PTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [Fact]
        public void DiskSlaveOptions_P2PProperties_DefaultAndCustomValues()
        {
            var options = new DiskSlaveOptions
            {
                LocalDirectory = _tempDir,
                UseP2P = true,
                P2PServerAddress = "123.45.67.89:4001",
                TargetPeerId = "12D3KooWMasterTestPeer",
                IdentitySeed = "my-secret-slave-seed",
                P2PRemoteAddress = "/ip4/127.0.0.1/tcp/4001/p2p/12D3KooWTestPeer",
                P2PPort = 4005,
                EnableRelay = true,
                EnableDht = true,
                EnableMdns = true,
                EnableQuic = true
            };
            options.BootstrapPeers.Add("/ip4/1.2.3.4/tcp/4001");

            Assert.True(options.UseP2P);
            Assert.Equal("123.45.67.89:4001", options.P2PServerAddress);
            Assert.Equal("12D3KooWMasterTestPeer", options.TargetPeerId);
            Assert.Equal("my-secret-slave-seed", options.IdentitySeed);
            Assert.Equal("/ip4/127.0.0.1/tcp/4001/p2p/12D3KooWTestPeer", options.P2PRemoteAddress);
            Assert.Equal(4005, options.P2PPort);
            Assert.True(options.EnableRelay);
            Assert.True(options.EnableDht);
            Assert.True(options.EnableMdns);
            Assert.True(options.EnableQuic);
            Assert.Single(options.BootstrapPeers);
        }

        [Fact]
        public void DiskMasterOptions_P2PProperties_DefaultAndCustomValues()
        {
            var options = new DiskMasterOptions
            {
                UseP2P = true,
                P2PServerAddress = "123.45.67.89:4001",
                IdentitySeed = "my-master-fixed-seed",
                TargetPeerId = "12D3KooWSlavePeer",
                P2PPort = 4001,
                EnableRelay = true,
                EnableDht = true,
                EnableMdns = true,
                EnableQuic = false
            };
            options.BootstrapPeers.Add("/ip4/5.6.7.8/tcp/4001");

            Assert.True(options.UseP2P);
            Assert.Equal("123.45.67.89:4001", options.P2PServerAddress);
            Assert.Equal("my-master-fixed-seed", options.IdentitySeed);
            Assert.Equal("12D3KooWSlavePeer", options.TargetPeerId);
            Assert.Equal(4001, options.P2PPort);
            Assert.True(options.EnableRelay);
            Assert.True(options.EnableDht);
            Assert.True(options.EnableMdns);
            Assert.False(options.EnableQuic);
            Assert.Single(options.BootstrapPeers);
        }

        [Fact]
        public void P2PAddressHelper_ToMultiaddr_ConvertsCorrectly()
        {
            Assert.Equal("/ip4/1.2.3.4/tcp/9831", P2PAddressHelper.ToMultiaddr("1.2.3.4:9831"));
            Assert.Equal("/ip4/1.2.3.4/tcp/9831", P2PAddressHelper.ToMultiaddr("1.2.3.4"));
            Assert.Equal("/dns4/relay.apefree.org/tcp/9831", P2PAddressHelper.ToMultiaddr("relay.apefree.org:9831"));
            Assert.Equal("/ip4/1.2.3.4/tcp/9831/p2p/QmTest", P2PAddressHelper.ToMultiaddr("/ip4/1.2.3.4/tcp/9831/p2p/QmTest"));
        }

        [Fact]
        public void P2PAddressHelper_BuildRelayAddress_BuildsCorrectly()
        {
            var relayAddr = P2PAddressHelper.BuildRelayAddress("1.2.3.4:4001", "12D3KooWMaster");
            Assert.Equal("/ip4/1.2.3.4/tcp/4001/p2p-circuit/p2p/12D3KooWMaster", relayAddr);

            var fullServerRelay = P2PAddressHelper.BuildRelayAddress("/ip4/1.2.3.4/tcp/4001/p2p/QmServer", "12D3KooWMaster");
            Assert.Equal("/ip4/1.2.3.4/tcp/4001/p2p/QmServer/p2p-circuit/p2p/12D3KooWMaster", fullServerRelay);
        }

        [Fact]
        public void P2PAddressHelper_ParseSeed_ReturnsValid32Bytes()
        {
            Assert.Null(P2PAddressHelper.ParseSeed(null));
            Assert.Null(P2PAddressHelper.ParseSeed("  "));

            var seed1 = P2PAddressHelper.ParseSeed("custom-passphrase");
            Assert.NotNull(seed1);
            Assert.Equal(32, seed1.Length);

            // 同样口令计算结果相同
            var seed2 = P2PAddressHelper.ParseSeed("custom-passphrase");
            Assert.Equal(seed1, seed2);

            // 64 字符十六进制解析
            var hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            var seedHex = P2PAddressHelper.ParseSeed(hex);
            Assert.NotNull(seedHex);
            Assert.Equal(32, seedHex.Length);
            Assert.Equal(0x01, seedHex[0]);
            Assert.Equal(0xef, seedHex[31]);
        }

        [Fact]
        public void DiskSlaveNode_P2PInitialization_NodeCreatesSuccessfully()
        {
            var options = new DiskSlaveOptions
            {
                LocalDirectory = _tempDir,
                UseP2P = true,
                P2PServerAddress = "127.0.0.1:9831",
                TargetPeerId = "12D3KooWMockMasterPeerId"
            };

            using var slaveNode = new DiskSlaveNode(options);
            Assert.NotNull(slaveNode);
            Assert.True(slaveNode.Options.UseP2P);
            Assert.False(slaveNode.IsConnected);
            Assert.Null(slaveNode.LocalPeerId);
        }

        [Fact]
        public void DiskMasterNode_P2PInitialization_NodeCreatesSuccessfully()
        {
            var options = new DiskMasterOptions
            {
                UseP2P = true,
                P2PServerAddress = "127.0.0.1:9831",
                EnableRelay = false
            };

            using var masterNode = new DiskMasterNode(options);
            Assert.NotNull(masterNode);
            Assert.True(masterNode.Options.UseP2P);
            Assert.False(masterNode.IsListening);
            Assert.Null(masterNode.P2PLocalPeerId);
        }

        [Fact]
        public async Task TestBootstrapServerStart()
        {
            using var server = new STTech.BytesIO.P2P.BootstrapServer();
            server.Options.TcpPort = 4055;
            server.Options.EnableRelay = false;
            await server.StartAsync();
            Assert.True(server.IsListening);
            Assert.NotNull(server.LocalPeerId);
            await server.CloseAsync();
        }

        [Fact]
        public async Task TestBootstrapServerAndPeerClientConnection()
        {
            using var server = new STTech.BytesIO.P2P.BootstrapServer();
            server.Options.ListenAddress = "127.0.0.1";
            server.Options.TcpPort = 4056;
            server.Options.EnableRelay = false;
            await server.StartAsync();

            var peerConnectedTcs = new TaskCompletionSource<STTech.BytesIO.P2P.PeerClient>();
            server.PeerConnected += (s, e) =>
            {
                peerConnectedTcs.TrySetResult(e.Client);
            };

            using var client = new STTech.BytesIO.P2P.PeerClient();
            client.RemoteAddress = $"/ip4/127.0.0.1/tcp/4056/p2p/{server.LocalPeerId}";
            client.Options.EnableRelay = false;

            var connectResult = await client.ConnectAsync(new STTech.BytesIO.Core.ConnectArgument { Timeout = 10000 });
            Assert.True(connectResult.IsSuccess, $"Connect failed: {connectResult.ErrorCode}");

            var connectedServerClient = await peerConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(connectedServerClient);

            await server.CloseAsync();
        }

        [Fact]
        public async Task TestP2PServerDualClientBridging()
        {
            using var server = new STTech.BytesIO.P2P.BootstrapServer();
            server.Options.ListenAddress = "127.0.0.1";
            server.Options.TcpPort = 4057;
            server.Options.EnableRelay = false;
            await server.StartAsync();

            STTech.BytesIO.P2P.PeerClient? client1ServerSide = null;
            STTech.BytesIO.P2P.PeerClient? client2ServerSide = null;
            var bridgeReadyTcs = new TaskCompletionSource<bool>();

            server.PeerConnected += (s, e) =>
            {
                if (client1ServerSide == null)
                {
                    client1ServerSide = e.Client;
                }
                else if (client2ServerSide == null)
                {
                    client2ServerSide = e.Client;

                    // 建立双向桥接
                    client1ServerSide.OnDataReceived += (sender, args) => client2ServerSide.Send(args.Data.Memory.ToArray());
                    client2ServerSide.OnDataReceived += (sender, args) => client1ServerSide.Send(args.Data.Memory.ToArray());

                    bridgeReadyTcs.TrySetResult(true);
                }
            };

            // 客户端 1 (模拟 Master)
            using var client1 = new STTech.BytesIO.P2P.PeerClient();
            client1.RemoteAddress = $"/ip4/127.0.0.1/tcp/4057/p2p/{server.LocalPeerId}";
            client1.Options.EnableRelay = false;
            var conn1 = await client1.ConnectAsync(new STTech.BytesIO.Core.ConnectArgument { Timeout = 10000 });
            Assert.True(conn1.IsSuccess);

            // 客户端 2 (模拟 Slave)
            using var client2 = new STTech.BytesIO.P2P.PeerClient();
            client2.RemoteAddress = $"/ip4/127.0.0.1/tcp/4057/p2p/{server.LocalPeerId}";
            client2.Options.EnableRelay = false;
            var conn2 = await client2.ConnectAsync(new STTech.BytesIO.Core.ConnectArgument { Timeout = 10000 });
            Assert.True(conn2.IsSuccess);

            await bridgeReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 测试跨中继双向数据传输
            var client2ReceivedTcs = new TaskCompletionSource<byte[]>();
            client2.OnDataReceived += (s, e) => client2ReceivedTcs.TrySetResult(e.Data.Memory.ToArray());

            await client1.SendAsync(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            var receivedData = await client2ReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, receivedData);

            await server.CloseAsync();
        }

        [Fact]
        public async Task TestServerLocalPeerIdMatchesIdentitySeed()
        {
            var seed = "test-fixed-seed";
            var seedBytes = P2PAddressHelper.ParseSeed(seed);
            var expectedPeerId = new Nethermind.Libp2p.Core.Identity(seedBytes).PeerId.ToString();

            using var server = new STTech.BytesIO.P2P.BootstrapServer();
            server.Options.TcpPort = 4060;
            server.Options.EnableRelay = false;
            server.Options.IdentitySeed = seedBytes;
            await server.StartAsync();

            Assert.Equal(expectedPeerId, server.LocalPeerId);
            await server.CloseAsync();
        }

        [Fact]
        public void TestResolveServerMultiaddr()
        {
            var defaultPeerId = P2PAddressHelper.GetPeerIdFromSeed(P2PAddressHelper.DefaultServerSeed);
            Assert.NotNull(defaultPeerId);

            // 1. 缺省使用默认服务端种子推导
            var resolvedDefault = P2PAddressHelper.ResolveServerMultiaddr("127.0.0.1:9831");
            Assert.Equal($"/ip4/127.0.0.1/tcp/9831/p2p/{defaultPeerId}", resolvedDefault);

            // 2. 显式指定 serverPeerId
            var resolvedWithPeer = P2PAddressHelper.ResolveServerMultiaddr("127.0.0.1:9831", serverPeerId: "12D3KooWCustomPeerId");
            Assert.Equal("/ip4/127.0.0.1/tcp/9831/p2p/12D3KooWCustomPeerId", resolvedWithPeer);

            // 3. 显式指定 serverSeed
            var customPeerId = P2PAddressHelper.GetPeerIdFromSeed("MyServerSeed");
            var resolvedWithSeed = P2PAddressHelper.ResolveServerMultiaddr("127.0.0.1:9831", serverSeed: "MyServerSeed");
            Assert.Equal($"/ip4/127.0.0.1/tcp/9831/p2p/{customPeerId}", resolvedWithSeed);

            // 4. 地址中内嵌 /p2p/
            var resolvedEmbedded = P2PAddressHelper.ResolveServerMultiaddr("127.0.0.1:9831/p2p/12D3KooWEmbedded");
            Assert.Equal("/ip4/127.0.0.1/tcp/9831/p2p/12D3KooWEmbedded", resolvedEmbedded);

            // 5. 完整 Multiaddr 保留原状
            var fullAddr = "/ip4/117.72.193.36/tcp/9831/p2p/12D3KooWFull";
            var resolvedFull = P2PAddressHelper.ResolveServerMultiaddr(fullAddr);
            Assert.Equal(fullAddr, resolvedFull);
        }

        [Fact]
        public async Task TestMasterNodeConnectsToBootstrapServerDefaultSeed()
        {
            // 启动 P2P 引导节点（模拟 ApeFree.DonateDisk.WinCli --p2p-server -p 4062）
            using var server = new STTech.BytesIO.P2P.BootstrapServer();
            server.Options.ListenAddress = "127.0.0.1";
            server.Options.TcpPort = 4062;
            server.Options.EnableRelay = false;
            server.Options.IdentitySeed = P2PAddressHelper.ParseSeed(P2PAddressHelper.DefaultServerSeed);
            await server.StartAsync();

            // 启动 Master 节点连接（模拟 ApeFree.DonateDisk.WinCli -m -p2p --server "127.0.0.1:4062" -d Z）
            var masterOptions = new DiskMasterOptions
            {
                UseP2P = true,
                P2PServerAddress = "127.0.0.1:4062",
                IdentitySeed = "A123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
                EnableRelay = false
            };

            using var master = new DiskMasterNode(masterOptions);
            await master.StartAsync();

            Assert.True(master.IsListening);
            Assert.NotNull(master.P2PLocalPeerId);

            await server.CloseAsync();
        }

        [Fact]
        public async Task TestP2PBridgeMount_MasterStartsFirst()
        {
            using var server = new STTech.BytesIO.P2P.BootstrapServer();
            server.Options.ListenAddress = "127.0.0.1";
            server.Options.TcpPort = 4070;
            server.Options.EnableRelay = false;
            server.Options.IdentitySeed = P2PAddressHelper.ParseSeed(P2PAddressHelper.DefaultServerSeed);

            STTech.BytesIO.P2P.PeerClient? client1 = null;
            STTech.BytesIO.P2P.PeerClient? client2 = null;
            object bridgeLock = new object();
            server.PeerConnected += (s, e) =>
            {
                var c = e.Client;
                lock (bridgeLock)
                {
                    if (client1 == null || !client1.IsConnected)
                    {
                        client1 = c;
                    }
                    else if (client2 == null || !client2.IsConnected)
                    {
                        client2 = c;
                        client1.OnDataReceived += (s1, a1) => client2?.Send(a1.Data.Memory.ToArray());
                        client2.OnDataReceived += (s2, a2) => client1?.Send(a2.Data.Memory.ToArray());
                    }
                }
            };
            await server.StartAsync();

            var tempDir = Path.Combine(Path.GetTempPath(), "p2p_bridge_test1_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. 先启动 Master (Master 先连入 P2P 服务端并等待 Slave)
                var masterOptions = new DiskMasterOptions
                {
                    UseP2P = true,
                    P2PServerAddress = "127.0.0.1:4070",
                    EnableRelay = false,
                    MountAsRemovable = true
                };
                using var master = new DiskMasterNode(masterOptions);
                var mountSuccessTcs = new TaskCompletionSource<bool>();
                var mountFailedTcs = new TaskCompletionSource<string>();
                master.SlaveMounted += (s, e) => mountSuccessTcs.TrySetResult(true);
                master.MountFailed += (s, e) => mountFailedTcs.TrySetResult(e.Exception.Message);

                await master.StartAsync();

                // 2. 延迟 800ms 后 Slave 才连入中继服务端
                await Task.Delay(800);

                var slaveOptions = new DiskSlaveOptions
                {
                    UseP2P = true,
                    P2PServerAddress = "127.0.0.1:4070",
                    LocalDirectory = tempDir,
                    EnableRelay = false
                };
                using var slave = new DiskSlaveNode(slaveOptions);
                var slaveStarted = await slave.StartAsync();
                Assert.True(slaveStarted);

                var completed = await Task.WhenAny(mountSuccessTcs.Task, mountFailedTcs.Task, Task.Delay(8000));
                if (completed == mountFailedTcs.Task)
                {
                    var msg = await mountFailedTcs.Task;
                    if (msg.Contains("winfsp", StringComparison.OrdinalIgnoreCase) || msg.Contains("Fsp.Interop.Api", StringComparison.OrdinalIgnoreCase))
                    {
                        // 当前系统未安装 WinFsp 内核驱动，符合无驱动环境预期
                        return;
                    }
                    Assert.Fail($"Mount failed with error: {msg}");
                }
                else if (completed == mountSuccessTcs.Task)
                {
                    Assert.True(await mountSuccessTcs.Task);
                }
                else
                {
                    Assert.Fail("Timed out waiting for mount or mount fail event");
                }
            }
            finally
            {
                await server.CloseAsync();
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task TestP2PBridgeMount_SlaveStartsFirst()
        {
            using var server = new STTech.BytesIO.P2P.BootstrapServer();
            server.Options.ListenAddress = "127.0.0.1";
            server.Options.TcpPort = 4071;
            server.Options.EnableRelay = false;
            server.Options.IdentitySeed = P2PAddressHelper.ParseSeed(P2PAddressHelper.DefaultServerSeed);

            STTech.BytesIO.P2P.PeerClient? client1 = null;
            STTech.BytesIO.P2P.PeerClient? client2 = null;
            object bridgeLock = new object();
            server.PeerConnected += (s, e) =>
            {
                var c = e.Client;
                lock (bridgeLock)
                {
                    if (client1 == null || !client1.IsConnected)
                    {
                        client1 = c;
                    }
                    else if (client2 == null || !client2.IsConnected)
                    {
                        client2 = c;
                        client1.OnDataReceived += (s1, a1) => client2?.Send(a1.Data.Memory.ToArray());
                        client2.OnDataReceived += (s2, a2) => client1?.Send(a2.Data.Memory.ToArray());
                    }
                }
            };
            await server.StartAsync();

            var tempDir = Path.Combine(Path.GetTempPath(), "p2p_bridge_test2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. 先启动 Slave
                var slaveOptions = new DiskSlaveOptions
                {
                    UseP2P = true,
                    P2PServerAddress = "127.0.0.1:4071",
                    LocalDirectory = tempDir,
                    EnableRelay = false
                };
                using var slave = new DiskSlaveNode(slaveOptions);
                var slaveStarted = await slave.StartAsync();
                Assert.True(slaveStarted);

                // 2. 延迟 500ms 后启动 Master
                await Task.Delay(500);

                var masterOptions = new DiskMasterOptions
                {
                    UseP2P = true,
                    P2PServerAddress = "127.0.0.1:4071",
                    EnableRelay = false,
                    MountAsRemovable = true
                };
                using var master = new DiskMasterNode(masterOptions);
                var mountSuccessTcs = new TaskCompletionSource<bool>();
                var mountFailedTcs = new TaskCompletionSource<string>();
                master.SlaveMounted += (s, e) => mountSuccessTcs.TrySetResult(true);
                master.MountFailed += (s, e) => mountFailedTcs.TrySetResult(e.Exception.Message);

                await master.StartAsync();

                var completed = await Task.WhenAny(mountSuccessTcs.Task, mountFailedTcs.Task, Task.Delay(8000));
                if (completed == mountFailedTcs.Task)
                {
                    var msg = await mountFailedTcs.Task;
                    if (msg.Contains("winfsp", StringComparison.OrdinalIgnoreCase) || msg.Contains("Fsp.Interop.Api", StringComparison.OrdinalIgnoreCase))
                    {
                        // 当前系统未安装 WinFsp 内核驱动，符合无驱动环境预期
                        return;
                    }
                    Assert.Fail($"Mount failed with error: {msg}");
                }
                else if (completed == mountSuccessTcs.Task)
                {
                    Assert.True(await mountSuccessTcs.Task);
                }
                else
                {
                    Assert.Fail("Timed out waiting for mount or mount fail event");
                }
            }
            finally
            {
                await server.CloseAsync();
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void P2PRelayManager_RegisterAndUnregister_HandlesLifecycleCorrectly()
        {
            using var relay = new P2PRelayManager();
            Assert.Equal(0, relay.ActivePeerCount);

            using var client1 = new STTech.BytesIO.P2P.PeerClient();
            using var client2 = new STTech.BytesIO.P2P.PeerClient();

            int changedCount = 0;
            relay.ActivePeerCountChanged += (s, count) => changedCount = count;

            relay.RegisterClient(client1);
            Assert.Equal(1, relay.ActivePeerCount);
            Assert.Equal(1, changedCount);

            relay.RegisterClient(client2);
            Assert.Equal(2, relay.ActivePeerCount);
            Assert.Equal(2, changedCount);

            // 模拟 client2 断开注销
            relay.UnregisterClient(client2);
            Assert.Equal(1, relay.ActivePeerCount);
            Assert.Equal(1, changedCount);

            // 模拟 client2 重连后再次注册
            relay.RegisterClient(client2);
            Assert.Equal(2, relay.ActivePeerCount);
            Assert.Equal(2, changedCount);

            // 释放管理器
            relay.Dispose();
            Assert.Equal(0, relay.ActivePeerCount);
        }

        [Fact]
        public void DiskSlaveNode_ReconnectConfiguration_HasCorrectDefaults()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "slave_reconnect_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var options = new DiskSlaveOptions
                {
                    UseP2P = true,
                    P2PServerAddress = "127.0.0.1:4099",
                    LocalDirectory = tempDir
                };

                using var slave = new DiskSlaveNode(options);
                Assert.Equal(3000, slave.ReconnectInterval);

                slave.ReconnectInterval = 1500;
                Assert.Equal(1500, slave.ReconnectInterval);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}
