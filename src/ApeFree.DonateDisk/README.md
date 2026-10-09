# ApeFree.DonateDisk

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0--windows-purple.svg)](https://dotnet.microsoft.com/)

## 1. 项目简介 (Introduction)

`ApeFree.DonateDisk` 是 ApeFree 旗下的远程主动捐赠式虚拟磁盘核心类库。基于全双工直连 RPC 机制（`ApeFree.ApeRpc.BytesIO`）与 Windows 内核用户态文件系统驱动（Dokany / `DokanNet`），实现 Slave 端在复杂内网环境下主动将本地目录作为虚拟磁盘发布，Master 端接收连接并自动挂载为 Windows 原生盘符。

## 2. 🌟 核心特性 (Key Features)

- **反向 RPC 拓扑**：Slave 主动拨号连出，天然穿透 NAT 与防火墙，Master 端直接获取代理对象控制远程文件系统。
- **Dokany 驱动挂载桥接**：将 Dokan 内核回调无损映射到 RPC 协议，Master 挂载为本地物理盘符（如 `Z:\`）。
- **Slave 绿色免驱动**：仅 Master 端需要驱动，Slave 端基于纯 .NET `System.IO` 实现，零系统侵入。
- **多 Slave 动态调度**：内置 `DriveLetterManager`，从 `Z:` 向 `D:` 自动分配与释放可用盘符。
- **安全沙箱防穿越**：内置 `SafePathResolver`，严格隔离目录穿越攻击并支持只读保护。

## 3. 🚀 快速上手 (Quick Start)

### Slave 端（发起捐赠）
```csharp
using ApeFree.DonateDisk.Slave;

var options = new DiskSlaveOptions
{
    TargetHost = "192.168.1.100",
    TargetPort = 9821,
    LocalDirectory = @"D:\MySharedData",
    VolumeLabel = "RemoteDisk"
};

using var slaveNode = new DiskSlaveNode(options);
bool success = await slaveNode.StartAsync();
```

### Master 端（接收并挂载）
```csharp
using ApeFree.DonateDisk.Master;

var options = new DiskMasterOptions
{
    ListenHost = "0.0.0.0",
    ListenPort = 9821,
    PreferredDriveLetter = 'Z',
    MountAsRemovable = true
};

using var masterNode = new DiskMasterNode(options);
masterNode.SlaveMounted += (sender, session) =>
{
    Console.WriteLine($"已挂载到盘符: {session.MountPoint}");
};
await masterNode.StartAsync();
```

## 4. 📄 开源许可证 (License)

Apache-2.0
