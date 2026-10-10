# ApeFree.DonateDisk

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

## 1. 项目简介 (Introduction)

**ApeFree.DonateDisk** 是一个面向 Windows 平台的高性能**主动式远程磁盘分享与本地虚拟磁盘挂载系统**。

在传统文件共享（如 Windows SMB 协议、NFS）中，通常需要服务端开放端口监听（如 445 端口），并在跨公网或复杂 NAT 环境下面临端口被运营商阻断、多层路由端口映射繁琐等痛点。普通网盘工具则往往无法作为 Windows 原生逻辑驱动器盘符（如 `Z:\`）供所有底层程序、命令行与开发工具直接无感读写。

本项目基于**全双工反向 RPC（Reverse RPC）**理念：
- **Slave 端（贡献者）**：无需公网 IP，在任何局域网或 NAT 内网下主动拨号连接到指定的 Master 端，将本地的物理文件夹作为“虚拟磁盘”主动捐赠出去；
- **Master 端（挂载者）**：通过高性能网络监听接收 Slave 连入，利用 **Dokany (DokanNet)** 用户态文件系统驱动，自动分配 Windows 空闲盘符（如 `Z:\`、`Y:\`）挂载为原生磁盘。Master 本地用户或程序操作该盘符如同操作本机硬盘。

---

## 2. 🌟 核心特性 (Key Features)

* **全双工直连 RPC**：底层基于 `ApeFree.ApeRpc.BytesIO` 与 `STTech.BytesIO`，网络连接由 Slave 主动连出，但应用层由 Master 驱动读取/写入 Slave，天然穿透 NAT/防火墙。
* **原生盘符无感访问**：基于 Dokany 内核级驱动拦截，挂载为 Windows 标准驱动器盘符（如 `Z:\`），完美兼容 Windows 资源管理器、PowerShell、CMD 及各类桌面软件。
* **轻量纯契约核心库**：核心库 `ApeFree.DonateDisk` 为纯契约接口与 DTO 模型（跨平台 .NET 10），不依赖 Dokany；仅 `WinCli` 终端按需依赖 DokanNet。
* **单文件修剪可执行程序**：`ApeFree.DonateDisk.WinCli` 提供合二为一的轻量可执行程序，支持命令行参数（`-m` / `-s`）与交互式模式快速切换。
* **流式分块高吞吐**：文件读写原生采用分块 I/O（Chunked I/O），配合高效二进制协议帧，低内存占用且支持大文件高速传输。
* **路径安全沙箱**：Slave 端内置 `SafePathResolver`，严格隔离目录穿越（Directory Traversal）攻击，支持一键只读保护（`--readonly`）。
* **自动化生命周期**：多 Slave 动态并发支持；自动探测并倒序分配空闲可用盘符（Z -> D）；Slave 离线或断开时自动安全卸载并释放盘符。

---

## 3. 架构拓扑 (Architecture)

```
┌──────────────────────────────────────────────────────────────┐
│                    Master 端 (挂载与消费)                     │
│  Windows Explorer / 应用程序 ──[Win32 I/O]──> Dokan 内核驱动  │
│                                                     │        │
│                                              DokanNet 桥接   │
│                                                     │        │
│  ApeRpc Master (TcpServer) <──[代理调度]── DokanDiskOperations│
└───────────────────────────────┬──────────────────────────────┘
                                │ TCP 全双工直连信道
                                │ (ApeRpc 协议帧)
┌───────────────────────────────▼──────────────────────────────┐
│                    Slave 端 (数据所有与提供)                   │
│  ApeRpc Slave (TcpClient) ────> IDiskOperationsService       │
│                                         │                    │
│                                    路径安全沙箱               │
│                                         │                    │
│                               本地物理磁盘 (System.IO)        │
└──────────────────────────────────────────────────────────────┘
```

---

## 4. 工程组成 (Projects)

| 工程 | 说明 | 运行形态 |
| :--- | :--- | :--- |
| **`ApeFree.DonateDisk`** | 纯契约接口核心库，定义远程虚拟磁盘 RPC 契约、事件及 DTO 数据模型 | 跨平台类库 (`net10.0`) |
| **`ApeFree.DonateDisk.WinCli`** | 综合命令行终端，内置 Master 挂载端与 Slave 分享端完整实现及 DokanNet 驱动适配 | 单文件修剪控制台 EXE (`net10.0-windows`) |
| **`ApeFree.DonateDisk.Tests`** | 完整单元与端到端集成测试套件 | xUnit 测试工程 (`net10.0-windows`) |

---

## 5. 快速上手 (Quick Start)

### 5.1 环境前置要求
1. **.NET 10.0 Runtime / SDK** (Windows x64)
2. **Master 端**：需安装 [Dokany 运行时驱动](https://github.com/dokan-dev/dokany/releases)（推荐 2.x 版本）。
   *(注：Slave 端无需安装 Dokany)*

### 5.2 启动 Master 端 (挂载监听)
在 Master 机器上执行：
```bash
# 以 Master 服务端模式运行，监听 9821 端口，默认首选盘符为 Z:
ApeFree.DonateDisk.WinCli.exe -m -p 9821 -d Z

# 或指定监听 IP（例如绑定局域网网卡）：
ApeFree.DonateDisk.WinCli.exe -m -i 192.168.1.100 -p 9821 -d Z
```

### 5.3 启动 Slave 端 (主动捐赠)
在 Slave 机器上执行：
```bash
# 以 Slave 客户端模式运行，将本地 D:\MyProjects 目录主动分享给 Master
ApeFree.DonateDisk.WinCli.exe -s -h 192.168.1.100 -p 9821 -d "D:\MyProjects" -n "ProjectDisk"

# 若仅允许 Master 读取、禁止修改或删除，可开启只读保护：
ApeFree.DonateDisk.WinCli.exe -s -h 192.168.1.100 -p 9821 -d "D:\PublicDocs" -r
```

### 5.4 交互模式
直接双击或不带参数运行 `ApeFree.DonateDisk.WinCli.exe` 即可进入交互式向导：
```text
=================== 请选择运行模式 ===================
  [1] Master 挂载服务端 - 监听网络并自动挂载远程 Slave 捐赠的磁盘
  [2] Slave  分享客户端 - 主动将本地目录分享给 Master 挂载使用
======================================================
请输入选项 [1/2] (默认 1):
```

### 5.5 访问虚拟磁盘
Master 端控制台将实时提示盘符就绪：
```text
[✓ 磁盘已挂载] 盘符 [Z:\] 就绪！
  - 客户端来源: 192.168.1.105:54321
  - 卷标名称:   ProjectDisk
  - 驱动器路径: 可以在 Windows 资源管理器或 CMD 中访问 Z:\
```
打开 Master 的“此电脑”即可看到 `Z:` 盘，双击即可无缝浏览与读写 Slave 上的文件！

---

## 6. 构建与打包 (Build & Release)

项目根目录下提供了自动化一键构建与打包脚本：
```powershell
.\build-release.ps1
```
脚本将自动在 `Install/` 目录中生成：
- **`ApeFree.DonateDisk.<Version>.nupkg`**：核心契约库的 NuGet 发布包；
- **`ApeFree.DonateDisk.WinCli.exe`**：修剪压缩后的单文件免安装可执行程序。

---

## 7. 开源许可证 (License)

本项目采用 [Apache-2.0](LICENSE) 许可证开源。
