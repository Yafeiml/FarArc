# FarArc 开发说明

FarArc 活动代码统一面向 .NET 10，并只在 Windows x64 上发布。

## 项目标识

修改代码或发布脚本时，应保持以下正式标识一致：

| 用途 | 标识 |
| --- | --- |
| 产品、程序集、解决方案、根命名空间 | `FarArc` |
| 测试项目和命名空间 | `FarArc.Tests` |
| 应用数据和默认数据库名 | `FarArc` |
| 外部运行器宏前缀 | `FARARC_` |
| 凭据密文格式 | `fararcsec:1` |
| 当前版本 | `0.1.1` |

`1Remote` 只能出现在来源、许可证、历史兼容性和迁移警告语境中。`PRemoteM` 仅作为明确的历史数据库导入功能名称保留。

## 环境要求

- Windows 10 版本 2004（build 19041）或更高；
- .NET 10 SDK；
- Visual Studio 2026 的 .NET 桌面开发工作负载（使用 IDE 时）；
- PowerShell。

仓库根目录的 `global.json` 固定 .NET 10，并允许滚动到已安装的最新 .NET 10 feature band。仓库已内置 Dragablz、Shawn.Utils、PuTTY 和固定版本的 VncSharpCore，不使用 Git submodule。

## 还原、构建与测试

在仓库根目录执行：

```powershell
dotnet restore .\FarArc.Tests\FarArc.Tests.csproj
dotnet build .\FarArc\FarArc.csproj -c Debug --no-restore --no-incremental
dotnet test .\FarArc.Tests\FarArc.Tests.csproj -c Debug --no-restore

dotnet restore .\Dragablz\Dragablz.Test\Dragablz.Test.csproj
dotnet test .\Dragablz\Dragablz.Test\Dragablz.Test.csproj -c Debug --no-restore

dotnet restore .\Dragablz\DragablzDemo\DragablzDemo.csproj
dotnet build .\Dragablz\DragablzDemo\DragablzDemo.csproj -c Debug --no-restore --no-incremental
```

## x64 发布

仅保留两个 Windows x64 发布配置：

```powershell
dotnet publish .\FarArc\FarArc.csproj -p:PublishProfile=x64-net100
dotnet publish .\FarArc\FarArc.csproj -p:PublishProfile=x64-net100-self-contained
```

Release 构建从 `C:\FarArc_Secret\EncryptionKey.txt` 读取加密密钥。该文件应只包含一个由至少 32 个随机字节生成的 Base64 字符串：

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

同一 FarArc 数据域的后续版本必须继续使用同一密钥；随意更换会让旧密文无法读取，除非先实现并执行明确的密钥轮换迁移。详细边界见 [SECURITY.md](SECURITY.md)。

## 版本

应用语义版本的唯一来源是 `FarArc/AppVersion.cs`。`FarArc.csproj` 中的产品版本、程序集版本和文件版本必须与其同步；三段语义版本 `0.1.1` 对应四段程序集/文件版本 `0.1.1.0`。

当前版本：

```text
0.1.1
```

## GitHub Actions

[`.github/workflows/fararc-release.yml`](.github/workflows/fararc-release.yml) 是唯一 CI/CD 工作流：

- Pull Request 和 `main` 分支提交执行构建、测试与依赖漏洞审计；
- 发布只由 `v*` 标签触发；
- 标签必须与 `scripts/Get-Version.ps1` 输出完全一致，例如 `v0.1.1`；
- Release 附带依赖运行时、自包含和完整对应源码三个 ZIP；
- 仓库必须配置 `GLOBAL_STRING_ENCRYPTION_KEY` Actions Secret。

## Invoke-Build（可选）

```powershell
Set-Alias ib $pwd\Invoke-Build.ps1
ib ?
ib Clean, Build -aReleaseType Debug
```

相关任务只安装 .NET 10 和 Windows 桌面开发工作负载，不包含 Store、MSIX 或 UWP 打包步骤。

## 来源与许可证

FarArc 是 [1Remote](https://github.com/1Remote/1Remote) 的独立派生项目。所有分发必须保留根目录 `LICENSE`、`FORK-NOTICE.md` 和适用的第三方许可证。修改来源关系、密文兼容性或发布材料时，应同时更新 [FORK-NOTICE.md](FORK-NOTICE.md)、[SECURITY.md](SECURITY.md) 和 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
