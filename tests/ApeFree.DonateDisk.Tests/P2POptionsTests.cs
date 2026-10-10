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
            Assert.Equal("/ip4/1.2.3.4/tcp/4001", P2PAddressHelper.ToMultiaddr("1.2.3.4:4001"));
            Assert.Equal("/ip4/1.2.3.4/tcp/4001", P2PAddressHelper.ToMultiaddr("1.2.3.4"));
            Assert.Equal("/dns4/relay.apefree.org/tcp/4001", P2PAddressHelper.ToMultiaddr("relay.apefree.org:4001"));
            Assert.Equal("/ip4/1.2.3.4/tcp/4001/p2p/QmTest", P2PAddressHelper.ToMultiaddr("/ip4/1.2.3.4/tcp/4001/p2p/QmTest"));
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
                P2PServerAddress = "127.0.0.1:4001",
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
                P2PServerAddress = "127.0.0.1:4001",
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
    }
}
