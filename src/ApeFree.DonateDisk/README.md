# ApeFree.DonateDisk

[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)

## 1. 项目简介 (Introduction)

`ApeFree.DonateDisk` 是 ApeFree 旗下的远程主动捐赠式虚拟磁盘核心契约类库。基于全双工直连 RPC 机制（`ApeFree.ApeRpc`），定义虚拟磁盘操作契约、文件系统变动事件以及数据传输模型（DTO）。

本类库为纯托管、轻量级、跨平台的契约库，不依赖任何原生内核驱动（如 Dokany）。

## 2. 🌟 核心契约与数据模型

- **`IDiskOperationsService`**：虚拟磁盘 RPC 核心服务契约，定义卷信息、路径元数据、文件打开、分块读写、文件修改/重命名/删除及时间属性等接口。
- **`FileChangeEventDto`**：远程文件系统变动事件数据对象，支持在 Slave 本地发生新建、修改、移动、删除等操作时主动推送到 Master。
- **`DiskItemInfoDto`** / **`DiskVolumeInfoDto`** / **`DiskFreeSpaceDto`**：标准化文件系统元数据传输对象。

## 3. 📄 开源许可证 (License)

Apache-2.0
