# 1Remote.NET10

<p align="center">
  <img src="Ui/Resources/Image/Logo/logo256.png" width="128" alt="1Remote.NET10 图标" />
</p>

`1Remote.NET10` 是由 **Yafeiml** 独立维护的 Windows 远程连接管理器，用一个桌面客户端集中管理 RDP、SSH、VNC、Telnet、FTP、SFTP、RemoteApp、串口和自定义外部程序连接。

本项目只面向 **.NET 10 / Windows x64**。它不使用原 1Remote 的官网、更新服务、Issue、Sentry 遥测、Microsoft Store 身份或发布流程；后续代码、版本与 Release 均在本仓库独立维护。

> [!CAUTION]
> **不能从原 1Remote 无损升级。** 不要覆盖原程序目录，不要让本项目直接连接原 SQLite、MySQL 或 PostgreSQL 数据库，也不要在没有完整备份的情况下尝试迁移。两者的凭据密文格式和密钥体系不兼容，目前没有自动转换工具。

## 下载与运行

请从本仓库的 [Releases](https://github.com/Yafeiml/1Remote/releases) 下载 Windows x64 压缩包：

- `*-net10-x64-self-contained.zip`：推荐，包含 .NET 10 运行时，解压后即可运行；
- `*-net10-x64.zip`：体积较小，需要预先安装 .NET 10 Desktop Runtime x64。

解压到一个全新目录后运行 `1Remote.NET10.exe`。本项目没有安装器、Microsoft Store 包或其他 CPU 架构的发布包。

## 与原 1Remote 的主要差异

| 项目 | 1Remote.NET10 | 原 1Remote |
| --- | --- | --- |
| 维护与发布 | 由 Yafeiml 在本仓库独立维护 | 由原项目维护者维护 |
| 运行平台 | 仅 .NET 10、Windows x64 | 存在其他历史框架和发布形式 |
| 应用数据标识 | `1RemoteNET10`，使用独立配置和数据目录 | `1Remote` |
| 程序集/产品名 | `1Remote.NET10` | `1Remote` |
| 凭据密文 | 内置 `rmsec:1`、AES-256-GCM、HKDF-SHA256 | 原 `1Remote.Security` 格式 |
| 数据库兼容性 | 不兼容，必须新建数据库 | 不适用于本项目 |
| VNC 依赖 | 对应源码固定内置于 `ThirdParty/VncSharpCore` | 原有外部依赖方式 |
| 遥测 | 不接入 Sentry，错误只写本地日志 | 原项目可能使用外部错误收集 |
| 应用内更新 | 默认关闭，不访问原更新地址 | 原项目更新机制 |
| 自动发布 | `main` 每次提交验证成功后自动生成 x64 预览 Release | 原项目发布流程 |

## 数据与升级边界

本项目特意使用新的内部应用标识 `1RemoteNET10`，以隔离本地配置、SQLite 数据库、启动项、命名管道和凭据相关数据。MySQL 与 PostgreSQL 的默认数据库名也改为 `1RemoteNET10`。

保存的密码、私钥、网关凭据和数据源密码使用版本化格式：

```text
rmsec:1:<key-id>:<nonce>:<ciphertext>:<authentication-tag>
```

原 1Remote 数据库中的加密字段不能被本项目直接解密。因此：

1. 先完整备份原程序目录、数据库和配置；
2. 为本项目新建空数据库或使用首次启动生成的新 SQLite 数据库；
3. 手工重新建立连接；如需迁移，只导出不含秘密的数据，并逐项核对；
4. 在独立副本中验证完毕前，不要修改原数据库；
5. 不要把两个程序配置为读写同一个数据库。

## 加密与未来同步

当前发布构建使用仓库 Actions Secret `GLOBAL_STRING_ENCRYPTION_KEY` 注入构建密钥。相同构建密钥生成的客户端可以读取同一份同步数据库，但该密钥存在于客户端二进制中，不能作为多租户商业同步服务的最终安全边界。

未来开发远程同步时，应升级为每用户或每保险库独立的数据密钥、主密码派生的密钥加密密钥、客户端本地解包和服务端仅保存密文的端到端方案。详细威胁边界与建议见 [SECURITY.md](SECURITY.md)。

## 本地构建

环境要求：

- Windows 10 版本 2004（10.0.19041）或更高；
- .NET 10 SDK；
- PowerShell。

```powershell
dotnet restore .\Tests\Tests.csproj
dotnet build .\Ui\Ui.csproj -c Debug --no-restore
dotnet test .\Tests\Tests.csproj -c Debug --no-restore
```

发布依赖 .NET 10 的 x64 包：

```powershell
dotnet publish .\Ui\Ui.csproj -p:PublishProfile=x64-net100
```

发布自包含 x64 包：

```powershell
dotnet publish .\Ui\Ui.csproj -p:PublishProfile=x64-net100-self-contained
```

Release 构建需要在 `C:\1Remote_Secret\EncryptionKey.txt` 放置至少 32 个随机字节的 Base64 编码密钥；Debug 构建和测试不要求本地密钥文件。

## 自动构建与发布

唯一发布工作流位于 `.github/workflows/publish-net10-x64.yml`：

- Pull Request 到 `main`：只执行还原、构建、测试和依赖漏洞审计；
- 提交到 `main`：验证通过后，自动发布两个 Windows x64 压缩包和一份对应源码包；
- 每次提交生成唯一的预览标签与 GitHub Release；
- 不包含夜间任务、定时发布、旧框架构建、Store/MSIX 发布或原项目下载步骤。

## 反馈与维护

- 问题反馈：[GitHub Issues](https://github.com/Yafeiml/1Remote/issues)
- 版本下载：[GitHub Releases](https://github.com/Yafeiml/1Remote/releases)
- 源码仓库：[Yafeiml/1Remote](https://github.com/Yafeiml/1Remote)

提交 Issue 前请说明使用的 Release 标签、Windows 版本、复现步骤，以及是否使用 SQLite、MySQL 或 PostgreSQL。不要上传真实密码、私钥、连接字符串或未脱敏数据库。

## 开源许可与来源说明

本项目是基于 1Remote 源码进行重大修改的派生作品，但不是原项目的官方版本，也不受原维护者认可、赞助或背书。派生代码继续按 [GNU General Public License v3.0](LICENSE) 发布；分发二进制时必须同时向接收者提供相应源码，并保留适用的版权和许可证声明。

项目名称、独立维护身份、关键修改记录和兼容性边界见 [FORK-NOTICE.md](FORK-NOTICE.md)，第三方组件说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。GPL 允许修改、再发布和商业运营，但要求满足源码提供、许可证保留和同许可证分发等义务；商标、图标、第三方素材及隐私合规仍需单独审查。
