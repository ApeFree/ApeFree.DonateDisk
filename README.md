# ApeFree.DonateDisk

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

## 1. 项目简介 (Introduction)

**ApeFree.DonateDisk** 是一个面向 Windows 平台的高性能**主动式远程磁盘分享与本地虚拟磁盘挂载系统**。

在传统文件共享（如 Windows SMB 协议、NFS）中，通常需要服务端开放端口监听（如 445 端口），并在跨公网或复杂 NAT 环境下面临端口被运营商阻断、多层路由端口映射繁琐等痛点。普通网盘工具则往往无法作为 Windows 原生逻辑驱动器盘符（如 `Z:\`）供所有底层程序、命令行与开发工具直接无感读写。

本项目提供两种灵活的网络互通方案：
1. **标准 TCP 直连模式**：适用于局域网或 Master 拥有公网 IP 环境，Slave 主动拨号连入 Master；
2. **P2P 去中心化中继模式 (Libp2p Circuit Relay)**：适用于 Master 与 Slave **双方均处于不同内网 NAT / 防火墙**之后，双方均无需公网 IP 和端口映射，只需**同时主动连入指定的公网 `p2p-server`**，即可自动建立端到端反向 RPC 通道并挂载原生磁盘！

---

## 2. 🌟 核心特性 (Key Features)

* **全双工直连 RPC**：底层基于 `ApeFree.ApeRpc.BytesIO` 与 `STTech.BytesIO`，支持反向 RPC 调度，天然穿透 NAT/防火墙。
* **原生盘符无感访问**：基于 Dokany 内核级驱动拦截，挂载为 Windows 标准驱动器盘符（如 `Z:\`），完美兼容 Windows 资源管理器、PowerShell、CMD 及各类桌面软件。
* **跨 NAT 双向连出 (P2P 模式)**：Master 与 Slave 均作为客户端主动连接 `p2p-server`，中继节点自动接通双向数据流，彻底解决多层内网穿透难题。
* **轻量纯契约核心库**：核心库 `ApeFree.DonateDisk` 为纯契约接口与 DTO 模型（跨平台 .NET 10），不依赖 Dokany；仅 `WinCli` 终端按需依赖 DokanNet。
* **单文件修剪可执行程序**：`ApeFree.DonateDisk.WinCli` 提供合二为一的轻量可执行程序，支持命令行参数（`-m` / `-s` / `--p2p-server`）与交互式向导快速切换。
* **流式分块高吞吐**：文件读写原生采用分块 I/O（Chunked I/O），配合高效二进制协议帧，低内存占用且支持大文件高速传输。
* **路径安全沙箱**：Slave 端内置 `SafePathResolver`，严格隔离目录穿越（Directory Traversal）攻击，支持一键只读保护（`--readonly`）。
* **自动化生命周期**：多 Slave 动态并发支持；自动探测并倒序分配空闲可用盘符（Z -> D）；Slave 离线或断开时自动安全卸载并释放盘符。

---

## 3. 架构拓扑 (Architecture)

### 3.1 标准 TCP 直连模式 (局域网 / 单向公网)
```
Master (TcpServer 监听 9821) <──────[TCP 主动连入]────── Slave (TcpClient 连出)
       │                                                      │
Dokany 挂载 (Z:\)                                      发布本地共享文件夹
```

### 3.2 P2P 去中心化中继模式 (双方均位于 NAT 内部)
```
                    ┌────────────────────────────┐
                    │   公网 P2P Server / Relay  │
                    │   (STTech.BytesIO.P2P)     │
                    └───────▲────────────▲───────┘
          主动连接 (Multiaddr)│            │主动连接 (Multiaddr + Circuit)
                            │            │
 ┌──────────────────────────┴─┐        ┌─┴──────────────────────────┐
 │   Master 端 (NAT 内部)     │        │    Slave 端 (NAT 内部)     │
 │  Windows 虚拟磁盘挂载 (Z:\) │ <─────>│   本地物理文件夹安全分享  │
 └────────────────────────────┘  ApeRpc └────────────────────────────┘
```

---

## 4. 工程组成 (Projects)

| 工程 | 说明 | 运行形态 |
| :--- | :--- | :--- |
| **`ApeFree.DonateDisk`** | 纯契约接口核心库，定义远程虚拟磁盘 RPC 契约、事件及 DTO 数据模型 | 跨平台类库 (`net10.0`) |
| **`ApeFree.DonateDisk.WinCli`** | 综合命令行终端，内置 Master 挂载端、Slave 分享端与独立 P2P 服务端完整实现 | 单文件修剪控制台 EXE (`net10.0-windows`) |
| **`ApeFree.DonateDisk.Tests`** | 完整单元与端到端集成测试套件 | xUnit 测试工程 (`net10.0-windows`) |

---

## 5. 快速上手 (Quick Start)

### 5.1 环境前置要求
1. **.NET 10.0 Runtime / SDK** (Windows x64)
2. **Master 端**：需安装 [Dokany 运行时驱动](https://github.com/dokan-dev/dokany/releases)（推荐 2.x 版本）。
   *(注：Slave 端无需安装 Dokany)*

---

### 5.2 方案 A：P2P 穿透模式 (推荐跨公网/复杂 NAT 使用)

#### 第一步：在公网 VPS 上运行 P2P 服务端 (或使用现成节点)
`WinCli` 已内置独立 P2P 引导与中继服务：
```bash
ApeFree.DonateDisk.WinCli.exe --p2p-server -p 4001
```
启动后终端将输出服务端的 Multiaddr 与 PeerId，例如：
```text
[P2P 服务端就绪] 本地 PeerId: 12D3KooWRelayServer...
[监听 Multiaddr] /ip4/1.2.3.4/tcp/4001
```

#### 第二步：Master 端主动连接 P2P 服务端并准备挂载
在 Master 机器（例如家庭电脑，处于家庭路由器内网）上执行：
```bash
# 连接公网 P2P 服务端，挂载盘符首选 Z:
ApeFree.DonateDisk.WinCli.exe -m -p2p --server "1.2.3.4:4001" -d Z

# (可选) 可通过 --seed 指定固定密钥种子，保持重启后 PeerId 不变：
ApeFree.DonateDisk.WinCli.exe -m -p2p --server "1.2.3.4:4001" --seed "my-master-key" -d Z
```
启动后终端将输出当前 Master 节点的唯一 PeerId：
```text
[P2P 就绪] Master 本地 PeerId: 12D3KooWMaster789...
```

#### 第三步：Slave 端连接 P2P 服务端并路由至 Master 捐赠磁盘
在 Slave 机器（例如公司电脑，处于企业防火墙内网）上执行：
```bash
# 连接公网 P2P 服务端，并通过中继指定捐赠给目标 Master：
ApeFree.DonateDisk.WinCli.exe -s -p2p -d "D:\MyShare" --server "1.2.3.4:4001" --target-peer "12D3KooWMaster789..."
```
连接成功后，Master 端电脑的“此电脑”将立刻自动弹出 `Z:` 盘！

---

### 5.3 方案 B：标准 TCP 直连模式 (局域网推荐)

#### Master 端 (挂载监听)
```bash
# 监听本地 9821 端口，默认分配 Z: 盘
ApeFree.DonateDisk.WinCli.exe -m -p 9821 -d Z
```

#### Slave 端 (主动捐赠)
```bash
# 主动连接 Master IP 并分享本地 D:\MyProjects 目录
ApeFree.DonateDisk.WinCli.exe -s -h 192.168.1.100 -p 9821 -d "D:\MyProjects" -n "ProjectDisk"

# (可选) 开启只读保护（禁止远端修改或删除文件）：
ApeFree.DonateDisk.WinCli.exe -s -h 192.168.1.100 -p 9821 -d "D:\PublicDocs" -r
```

---

### 5.4 交互引导模式
直接双击或不带参数运行 `ApeFree.DonateDisk.WinCli.exe` 即可进入交互式向导：
```text
=================== 请选择运行模式 ===================
  [1] Master 挂载服务端 - 监听网络或连接 P2P 服务端并自动挂载远程磁盘
  [2] Slave  分享客户端 - 主动将本地目录分享给 Master 挂载使用
  [3] P2P    引导中继端 - 启动独立公网 P2P 引导与中继服务 (Relay Server)
======================================================
请输入选项 [1/2/3] (默认 1):
```

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
