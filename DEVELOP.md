# 开发说明

活动代码统一面向 .NET 10，并只在 Windows 上构建和发布。

## 环境要求

- Windows 10 版本 2004（build 19041）或更高；
- .NET 10 SDK；
- Visual Studio 2026 的 .NET 桌面开发工作负载（使用 IDE 时）；
- PowerShell。

仓库根目录的 `global.json` 固定 .NET 10，并允许滚动到已安装的最新 .NET 10 feature band。仓库已内置 Dragablz、Shawn.Utils、PuTTY 和固定版本的 VncSharpCore，不使用 Git submodule。

## 还原、构建与测试

在仓库根目录执行：

```powershell
dotnet restore .\Tests\Tests.csproj
dotnet build .\Ui\Ui.csproj -c Debug --no-restore --no-incremental
dotnet test .\Tests\Tests.csproj -c Debug --no-restore

dotnet restore .\Dragablz\Dragablz.Test\Dragablz.Test.csproj
dotnet test .\Dragablz\Dragablz.Test\Dragablz.Test.csproj -c Debug --no-restore

dotnet restore .\Dragablz\DragablzDemo\DragablzDemo.csproj
dotnet build .\Dragablz\DragablzDemo\DragablzDemo.csproj -c Debug --no-restore --no-incremental
```

## x64 发布

仅保留两个 Windows x64 发布配置：

```powershell
dotnet publish .\Ui\Ui.csproj -p:PublishProfile=x64-net100
dotnet publish .\Ui\Ui.csproj -p:PublishProfile=x64-net100-self-contained
```

Release 构建从 `C:\1Remote_Secret\EncryptionKey.txt` 读取加密密钥。该文件应只包含一个由 32 个随机字节生成的 Base64 字符串：

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

同一项目的后续版本必须继续使用同一密钥；随意更换会让旧版本写入的密文无法读取，除非先实现并执行明确的密钥轮换迁移。详细边界见 `SECURITY.md`。

## GitHub Actions

`.github/workflows/publish-net10-x64.yml` 是唯一发布工作流：

- Pull Request 到 `main` 时执行构建、测试与依赖漏洞审计；
- 提交到 `main` 且验证成功时，自动创建唯一的预览 Release；
- Release 附带依赖运行时、自包含和完整对应源码三个 ZIP；
- 仓库需要配置 `GLOBAL_STRING_ENCRYPTION_KEY` Actions Secret。

## Invoke-Build（可选）

```powershell
Set-Alias ib $pwd\Invoke-Build.ps1
ib ?
ib Clean, Build -aReleaseType Debug
```

相关任务只安装 .NET 10 和 Windows 桌面开发工作负载，不包含 Store、MSIX 或 UWP 打包步骤。
