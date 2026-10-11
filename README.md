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
2. **Master 端**：需安装 [Dokany 运行时驱动](https://github.com/dokan-dev/dokany/releases)（推荐 2.x 版本），用于 Windows 内核级虚拟磁盘拦截与原生盘符挂载。  
   *(注：Slave 分享端与 P2P 服务端均无需安装 Dokany)*

---

### 5.2 方案 A：P2P 中继穿透模式 (跨公网/复杂 NAT 穿透首选，推荐)

> **💡 架构优势说明**：
> 1. **内置默认种子，零配对负担**：P2P 模式内置了预设认证凭据种子，双方直接连入同一个 `--server` 即可自动完成握手建立中继；
> 2. **完全解耦启动时序**：服务端、Master 端与 Slave 端三者互不依赖启动顺序。无论是谁先开、谁后开，只要双方连入即可自动打通隧道完成磁盘挂载；
> 3. **断线无限自动重连**：在弱网、网络波动中断时，后台每 3 秒自动无限重试重连，恢复后 Master 端自动无缝重新挂载并恢复盘符读写。

#### 建议写法（推荐写法，使用内置种子）：

##### 第一步：启动公网 P2P 引导与中继服务端 (Relay Server)
```bash
ApeFree.DonateDisk.WinCli.exe --p2p-server -p 9831
```
*(在具有公网 IP 的云服务器或中继节点上运行，监听 9831 端口)*

##### 第二步：启动 Master 端 (远程虚拟磁盘挂载端)
```bash
ApeFree.DonateDisk.WinCli.exe -m -p2p --server "127.0.0.1:9831" -d Z
```
*(连接 P2P 服务端，指定首选驱动器盘符为 `Z:\`)*

##### 第三步：启动 Slave 端 (本地目录主动分享客户端)
```bash
ApeFree.DonateDisk.WinCli.exe -s -p2p --server "127.0.0.1:9831" -d "C:\Test"
```
*(连接 P2P 服务端，将本地 `C:\Test` 目录分享给 Master。连接成功后，Master 机器的“此电脑”将立刻自动弹出 `Z:` 盘！)*

---

#### 可选写法（自定义网络口令 / 固定 PeerId）：
由于系统内置了预设种子，普通场景无需额外指定种子。若需要隔离多个独立租户或自定义专属访问密钥，可通过 `--seed` 自定义身份私钥种子：

```bash
# 1. 服务端指定自定义凭据种子
ApeFree.DonateDisk.WinCli.exe --p2p-server -p 9831 --seed "MyCustomSecret123"

# 2. Master 端指定相同凭据种子
ApeFree.DonateDisk.WinCli.exe -m -p2p --server "127.0.0.1:9831" --seed "MyCustomSecret123" -d Z

# 3. Slave 端指定相同凭据种子
ApeFree.DonateDisk.WinCli.exe -s -p2p --server "127.0.0.1:9831" --seed "MyCustomSecret123" -d "C:\Test"
```

---

### 5.3 方案 B：标准 TCP 直连模式 (局域网 / 单向公网直连)

适用于局域网内部互联或 Master 机器拥有可直接访问的 IP 与端口环境。

#### 第一步：Master 端 (监听本地端口等待连接)
```bash
# 本地监听 9821 端口，首选挂载为 Z: 盘
ApeFree.DonateDisk.WinCli.exe -m -p 9821 -d Z
```

#### 第二步：Slave 端 (主动拨号连接 Master)
```bash
# 主动连接 Master 端 IP:Port，分享本地 C:\Test 目录并指定卷标名称为 Test
ApeFree.DonateDisk.WinCli.exe -s -h 127.0.0.1 -p 9821 -d "C:\Test" -n "Test"

# (可选) 开启只读保护模式（禁止远端 Master 修改或删除本地文件）：
ApeFree.DonateDisk.WinCli.exe -s -h 127.0.0.1 -p 9821 -d "C:\Test" -n "Test" -r
```

---

### 5.4 交互向导模式 (零参数快速启动)
直接双击运行或在命令行不传任何参数启动 `ApeFree.DonateDisk.WinCli.exe`，即可进入交互式数字菜单导航：
```text
=================== 请选择运行模式 ===================
  [1] Master 挂载服务端 - 监听网络或连接 P2P 服务端并自动挂载远程磁盘
  [2] Slave  分享客户端 - 主动将本地目录分享给 Master 挂载使用
  [3] P2P    引导中继端 - 启动独立公网 P2P 引导与中继服务 (Relay Server)
======================================================
请输入选项 [1/2/3] (默认 1):
```

---

## 6. 命令行参数完整手册 (CLI Parameters Reference)

`ApeFree.DonateDisk.WinCli` 支持强大的命令行参数定制，所有模式与选项说明如下：

### 6.1 运行模式分派参数
| 选项 | 别名 | 描述 |
| :--- | :--- | :--- |
| `-m` | `--master` | 以 **Master 挂载服务端** 模式运行（连接网络并挂载 Windows 原生盘符） |
| `-s` | `--slave` | 以 **Slave 分享客户端** 模式运行（将本地物理文件夹分享至远端 Master） |
| `--p2p-server` | `-server`, `--relay-server` | 启动 **独立公网 P2P 引导与中继服务端** |
| `--help` | `-?`, `-h` | 打印全局或当前模式的帮助信息 |

---

### 6.2 Master 挂载服务端参数 (`-m, --master`)
| 参数 | 全称 | 默认值 | 详细含义说明 |
| :--- | :--- | :--- | :--- |
| `-d <盘符>` | `--drive` | `Z` | 首选挂载的驱动器盘符（如 `Z`）。若该盘符已被占用，系统将自动向下顺延至可用盘符（Z -> D） |
| `-p <端口>` | `--port` | `9821` | 本地 TCP 监听端口（仅在标准 TCP 直连模式下生效） |
| `-i <IP>` | `--ip` | `0.0.0.0` | 本地 TCP 监听绑定的网卡 IP 地址（仅在标准 TCP 直连模式下生效） |
| `--fixed` | | *(关闭)* | 挂载为 Windows 本地固定磁盘（FixedDrive）。默认挂载为可移动磁盘（RemovableDrive） |
| **`-p2p`** | `--p2p` | *(关闭)* | **开启 P2P (Libp2p) 去中心化中继穿透模式** |
| **`--server <地址>`** | `--p2p-server` | 无 | **P2P 服务端地址**（格式为 `IP:Port` 如 `127.0.0.1:9831` 或 Libp2p Multiaddr） |
| `--seed <种子>` | | 内置默认种子 | 本地身份私钥种子（字符串或 64 位 Hex 哈希），用于固定 PeerId 或建立专属网络通信凭据 |
| `--server-peer <PeerId>` | | 无 | （可选）显式指定 P2P 服务端的 PeerId |
| `--server-seed <种子>` | | 内置默认种子 | （可选）显式指定 P2P 服务端的生成种子 |
| `--target-peer <PeerId>` | `--slave-peer` | 无 | （可选）仅接受指定 PeerId 的 Slave 端挂载请求 |
| `--relay` / `--no-relay` | | `启用` | 是否启用 Libp2p Circuit Relay 中继穿透功能 |
| `--dht` | | *(关闭)* | 是否启用 Kademlia DHT 节点发现 |
| `--mdns` | | *(关闭)* | 是否启用 mDNS 局域网节点发现 |
| `--quic` | | *(关闭)* | 是否启用 QUIC 传输协议（默认采用 TCP 流传输） |
| `--bootstrap <地址>` | | 无 | 额外添加的 P2P 引导节点 Multiaddr 地址 |

---

### 6.3 Slave 分享客户端参数 (`-s, --slave`)
| 参数 | 全称 | 默认值 | 详细含义说明 |
| :--- | :--- | :--- | :--- |
| **`-d <路径>`** | `--dir` | *(必填)* | **待分享给 Master 挂载的本地物理文件夹路径**（支持绝对路径或相对路径） |
| `-h <主机>` | `--host` | `127.0.0.1` | 目标 Master 端的主机 IP 地址或域名（仅在标准 TCP 直连模式下生效） |
| `-p <端口>` | `--port` | `9821` | 目标 Master 端的监听端口（仅在标准 TCP 直连模式下生效） |
| `-n <名称>` | `--name` | 本地目录名 | 虚拟磁盘的卷标显示名称（Volume Label，显示在“此电脑”中的驱动器名） |
| `-r` | `--readonly` | *(关闭)* | **开启只读保护模式**（远端 Master 仅能读取，禁止创建、修改、重命名或删除文件） |
| **`-p2p`** | `--p2p` | *(关闭)* | **开启 P2P (Libp2p) 点对点中继传输模式** |
| **`--server <地址>`** | `--p2p-server` | 无 | **P2P 服务端地址**（格式为 `IP:Port` 如 `127.0.0.1:9831` 或 Multiaddr） |
| `--seed <种子>` | | 内置默认种子 | 本地身份私钥种子，保持本节点 PeerId 跨重启固定 |
| `--server-peer <PeerId>` | | 无 | （可选）显式指定 P2P 服务端的 PeerId |
| `--server-seed <种子>` | | 内置默认种子 | （可选）显式指定 P2P 服务端的生成种子 |
| `--target-peer <PeerId>` | `--master-peer` | 无 | （可选）通过 P2P 服务端中继定向寻址指定的 Master 端 PeerId |
| `--master-seed <种子>` | `--target-seed` | 无 | （可选）目标 Master 端的种子（自动计算对应的 Master PeerId） |
| `-addr <地址>` | `--remote-addr` | 无 | （可选）目标 Master 端的完整 Multiaddr 地址 |
| `--relay` / `--no-relay` | | `启用` | 是否启用 Libp2p Circuit Relay 中继穿透功能 |
| `--dht` | | *(关闭)* | 是否启用 Kademlia DHT 发现 |
| `--mdns` | | *(关闭)* | 是否启用 mDNS 局域网发现 |
| `--quic` | | *(关闭)* | 是否启用 QUIC 传输协议 |
| `--bootstrap <地址>` | | 无 | 额外添加的 P2P 引导节点 Multiaddr 地址 |

---

### 6.4 独立 P2P 引导与中继服务端参数 (`--p2p-server`)
| 参数 | 全称 | 默认值 | 详细含义说明 |
| :--- | :--- | :--- | :--- |
| `-p <端口>` | `--port` | `9831` | 服务端监听的网络端口 |
| `-i <IP>` | `--ip, --host` | `0.0.0.0` | 服务端监听绑定的 IP 地址 |
| `--seed <种子>` | | 内置默认种子 | 服务端身份私钥种子（留空时使用内置默认种子，双方客户端无需配置即可连入） |
| `--quic` | | *(关闭)* | 是否启用 QUIC 传输协议支持 |

---

## 7. 构建与打包 (Build & Release)

项目根目录下提供了自动化一键构建与打包脚本：
```powershell
.\build-release.ps1
```
脚本将自动在 `Install/` 目录中生成：
- **`ApeFree.DonateDisk.<Version>.nupkg`**：核心契约库的 NuGet 发布包；
- **`ApeFree.DonateDisk.WinCli.exe`**：修剪压缩后的单文件免安装可执行程序。

---

## 8. 开源许可证 (License)

本项目采用 [Apache-2.0](LICENSE) 许可证开源。
