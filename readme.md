# FarArc

<p align="center">
  <img src="FarArc/Resources/Image/Logo/logo256.png" width="128" alt="FarArc 图标" />
</p>

<p align="center">
  <strong>万端归一，连接远方。</strong><br />
  All your remote connections. One workspace.
</p>

<p align="center">
  <a href="https://github.com/Yafeiml/FarArc/actions/workflows/fararc-release.yml"><img alt="CI" src="https://github.com/Yafeiml/FarArc/actions/workflows/fararc-release.yml/badge.svg" /></a>
  <a href="LICENSE"><img alt="License: GPL-3.0" src="https://img.shields.io/badge/license-GPL--3.0-blue.svg" /></a>
  <img alt="Version 0.1.0" src="https://img.shields.io/badge/version-0.1.0-00a4ef.svg" />
</p>

`FarArc` 是面向 Windows 的统一远程连接工作区。它在一个桌面客户端中集中管理 RDP、SSH、VNC、Telnet、FTP、SFTP、RemoteApp、串口和自定义外部程序连接。

当前首发版本为 **0.1.0**，仅面向 **.NET 10 / Windows x64**。

> [!IMPORTANT]
> FarArc 是基于开源项目 [1Remote](https://github.com/1Remote/1Remote) 修改的独立派生项目，不是 1Remote 的官方版本，也不受其维护者认可、赞助或背书。项目保留 GPLv3、版权和来源说明；详细边界见 [FORK-NOTICE.md](FORK-NOTICE.md)。

> [!CAUTION]
> **FarArc 0.1.0 不能从上游 1Remote 或此前的 1Remote.NET10 开发构建原地升级。** 请使用全新程序目录和全新数据库。不要让这些程序读写同一数据库，也不要在没有完整离线备份的情况下尝试人工迁移。目前没有自动凭据转换工具。

## 功能

- 在统一的标签页工作区中管理和打开多种远程协议；
- 支持 RDP、SSH、VNC、Telnet、FTP、SFTP、RemoteApp 和串口；
- 支持自定义外部运行器、连接前后脚本和环境变量；
- 提供标签、图标、颜色、搜索、快速启动器和多语言界面；
- 支持 SQLite、MySQL 和 PostgreSQL 数据源；
- 凭据在写入数据库前使用 AES-256-GCM 认证加密；
- 默认不接入 Sentry，不使用上游更新、商店或发布基础设施。

## 下载与运行

请从 [GitHub Releases](https://github.com/Yafeiml/FarArc/releases) 下载 Windows x64 压缩包：

- `FarArc-0.1.0-win-x64-net10-self-contained.zip`：推荐，包含 .NET 10 运行时；
- `FarArc-0.1.0-win-x64-net10.zip`：体积较小，需要预先安装 .NET 10 Desktop Runtime x64；
- `FarArc-0.1.0-source.zip`：该版本对应的完整源码。

将程序解压到一个全新目录，运行 `FarArc.exe`。当前没有安装器、Microsoft Store 包或其他 CPU 架构的发布包。

## 独立身份与数据边界

FarArc 0.1.0 使用一套与来源项目隔离的身份：

| 项目 | FarArc 0.1.0 |
| --- | --- |
| 产品、程序集和根命名空间 | `FarArc` |
| 应用数据标识 | `FarArc` |
| AppData 目录 | `%APPDATA%\FarArc`、`%LOCALAPPDATA%\FarArc` |
| MySQL/PostgreSQL 默认数据库名 | `FarArc` |
| 外部运行器宏 | `%FARARC_HOSTNAME%`、`%FARARC_PORT%`、`%FARARC_USERNAME%`、`%FARARC_PASSWORD%`、`%FARARC_PRIVATE_KEY_PATH%` |
| 凭据密文头 | `fararcsec:1` |
| 仓库与反馈 | [Yafeiml/FarArc](https://github.com/Yafeiml/FarArc) |

保存的密码、私钥、网关凭据、机密应用参数和数据源密码使用以下版本化格式：

```text
fararcsec:1:<key-id>:<nonce>:<ciphertext>:<authentication-tag>
```

FarArc 0.1.0 同时更换了密文标识和密钥派生上下文，因此不会把上游 1Remote 或此前 `rmsec:1` 格式的数据误判为本项目密文。迁移时应遵循以下原则：

1. 先完整备份原程序目录、数据库和配置；
2. 为 FarArc 新建数据库，或使用首次启动生成的新 SQLite 数据库；
3. 手工重建含凭据的连接；如需迁移元数据，只在离线副本中处理并逐项核对；
4. 验证 FarArc 可独立运行前，不修改原数据库；
5. 永远不要让 FarArc 与其他客户端共同写入同一数据库。

## 安全边界

当前发布构建通过仓库 Actions Secret `GLOBAL_STRING_ENCRYPTION_KEY` 注入构建级密钥。相同密钥构建的客户端可以读取同一份数据库，但密钥存在于客户端二进制中，因此不能作为多租户同步服务的最终安全边界。

未来若提供远程同步，应迁移到每用户或每保险库独立数据密钥、主密码派生的密钥加密密钥、客户端本地解包以及服务端仅保存密文的端到端方案。完整威胁模型见 [SECURITY.md](SECURITY.md)。

## 本地构建

环境要求：

- Windows 10 版本 2004（10.0.19041）或更高；
- .NET 10 SDK；
- PowerShell。

在仓库根目录执行：

```powershell
dotnet restore .\FarArc.Tests\FarArc.Tests.csproj
dotnet build .\FarArc\FarArc.csproj -c Debug --no-restore --no-incremental
dotnet test .\FarArc.Tests\FarArc.Tests.csproj -c Debug --no-restore
```

发布依赖 .NET 10 的 x64 包：

```powershell
dotnet publish .\FarArc\FarArc.csproj -p:PublishProfile=x64-net100
```

发布自包含 x64 包：

```powershell
dotnet publish .\FarArc\FarArc.csproj -p:PublishProfile=x64-net100-self-contained
```

Release 构建需要在 `C:\FarArc_Secret\EncryptionKey.txt` 放置至少 32 个随机字节的 Base64 编码密钥；Debug 构建和测试不要求本地密钥文件。更多说明见 [DEVELOP.md](DEVELOP.md)。

## CI 与发布

唯一发布工作流是 [`.github/workflows/fararc-release.yml`](.github/workflows/fararc-release.yml)：

- Pull Request 和 `main` 分支提交执行还原、构建、测试与依赖漏洞审计；
- 正式发布只由与应用版本完全一致的 `v*` 标签触发；
- 首个发布标签为 `v0.1.0`；
- Release 同时附带依赖运行时包、自包含包和对应完整源码包；
- 不包含夜间任务、定时发布、Store/MSIX 发布或上游下载步骤。

## 反馈

- 问题反馈：[GitHub Issues](https://github.com/Yafeiml/FarArc/issues)
- 版本下载：[GitHub Releases](https://github.com/Yafeiml/FarArc/releases)
- 源码仓库：[Yafeiml/FarArc](https://github.com/Yafeiml/FarArc)

提交 Issue 时请说明 FarArc 版本、Windows 版本、复现步骤，以及使用的数据库类型。不要上传真实密码、私钥、连接字符串或未脱敏数据库。

## 许可证与来源

FarArc 包含基于 1Remote 的派生代码，并继续按 [GNU General Public License v3.0](LICENSE) 分发。向他人提供二进制时，必须同时按 GPLv3 要求提供该版本的完整对应源码，并保留适用的版权、许可证和来源声明。

- 上游项目：[1Remote/1Remote](https://github.com/1Remote/1Remote)
- 派生关系与重大修改：[FORK-NOTICE.md](FORK-NOTICE.md)
- 第三方组件与审计边界：[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- 安全设计与限制：[SECURITY.md](SECURITY.md)

GPL 处理版权许可，但不会自动授予商标、名称、图标、肖像或其他品牌素材的使用权。正式商业运营前仍应独立完成品牌、素材、依赖、隐私和出口合规审查。
