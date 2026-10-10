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
                P2PRemoteAddress = "/ip4/127.0.0.1/tcp/4001/p2p/12D3KooWTestPeer",
                P2PPort = 4005,
                P2PPeerId = "12D3KooWTestPeer",
                EnableRelay = true,
                EnableDht = true,
                EnableMdns = true,
                EnableQuic = true
            };
            options.BootstrapPeers.Add("/ip4/1.2.3.4/tcp/4001");

            Assert.True(options.UseP2P);
            Assert.Equal("/ip4/127.0.0.1/tcp/4001/p2p/12D3KooWTestPeer", options.P2PRemoteAddress);
            Assert.Equal(4005, options.P2PPort);
            Assert.Equal("12D3KooWTestPeer", options.P2PPeerId);
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
                P2PPort = 4001,
                EnableRelay = true,
                EnableDht = true,
                EnableMdns = true,
                EnableQuic = false
            };
            options.BootstrapPeers.Add("/ip4/5.6.7.8/tcp/4001");

            Assert.True(options.UseP2P);
            Assert.Equal(4001, options.P2PPort);
            Assert.True(options.EnableRelay);
            Assert.True(options.EnableDht);
            Assert.True(options.EnableMdns);
            Assert.False(options.EnableQuic);
            Assert.Single(options.BootstrapPeers);
        }

        [Fact]
        public void DiskSlaveNode_P2PInitialization_NodeCreatesSuccessfully()
        {
            var options = new DiskSlaveOptions
            {
                LocalDirectory = _tempDir,
                UseP2P = true,
                TargetHost = "127.0.0.1",
                P2PPort = 4001,
                P2PPeerId = "12D3KooWMockPeerId"
            };

            using var slaveNode = new DiskSlaveNode(options);
            Assert.NotNull(slaveNode);
            Assert.True(slaveNode.Options.UseP2P);
            Assert.False(slaveNode.IsConnected);
        }

        [Fact]
        public void DiskMasterNode_P2PInitialization_NodeCreatesSuccessfully()
        {
            var options = new DiskMasterOptions
            {
                UseP2P = true,
                P2PPort = 4001,
                EnableRelay = true
            };

            using var masterNode = new DiskMasterNode(options);
            Assert.NotNull(masterNode);
            Assert.True(masterNode.Options.UseP2P);
            Assert.False(masterNode.IsListening);
        }
    }
}
