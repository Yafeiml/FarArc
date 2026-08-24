# 独立派生项目与来源说明

**FarArc** 包含基于开源项目 [1Remote](https://github.com/1Remote/1Remote) 修改的派生代码。FarArc 由 Yafeiml 独立维护，不是 1Remote 的官方版本，也不受 1Remote 项目或其维护者认可、赞助或背书。

本仓库保留 `1Remote` 名称，仅用于准确描述来源、历史兼容边界、版权归属和许可证义务。FarArc 不使用上游的官网、更新服务、Issue、Sentry 遥测、Microsoft Store 身份或发布基础设施。

## 重大修改记录

当前独立派生基线日期：2026-08-24。

- 将正式产品、程序集、解决方案、项目和根命名空间统一更名为 `FarArc`；
- 将首个公开版本设为 `0.1.0`；
- 将活动应用、库、测试和示例统一到 .NET 10；
- 仅保留 Windows x64 发布配置；
- 移除 .NET Framework、.NET 6、.NET 8、.NET 9、夜间任务和旧发布流程；
- 使用独立应用数据标识和默认数据库名 `FarArc`；
- 将外部运行器宏迁移到 `%FARARC_*%`；
- 移除 `1Remote.Security` 依赖，使用仓库内版本化 AES-256-GCM 实现；
- 使用独立密文头 `fararcsec:1` 和 FarArc 专用 HKDF 上下文；
- 明确放弃上游加密数据库及此前 `rmsec:1` 开发格式的兼容性，不提供无损升级路径；
- 移除 Sentry 与上游网站、商店、更新和反馈连接；
- 将 VncSharpCore 对应源码固定内置，并在发布时同时提供完整对应源码；
- 由本仓库 Actions 验证代码，仅在推送匹配应用版本的 `v*` 标签时创建 Release。

未来维护者在分发重大改动时，应继续更新本记录。

## 数据不兼容声明

FarArc 0.1.0 的 `fararcsec:1` 密文不能解密以下数据：

- 上游 1Remote 及其原 `1Remote.Security` 凭据格式；
- 此前 1Remote.NET10 开发构建写入的 `rmsec:1` 格式；
- 使用不同构建密钥生成的 FarArc 密文。

请使用全新程序目录和全新数据库，不要让 FarArc 与上游 1Remote 或旧开发构建共同读写同一数据库。任何人工迁移都应先在离线副本中完成完整备份和验证。

## 许可证义务

来源于 1Remote 的派生代码继续按 GNU General Public License version 3 分发，完整条款见 [LICENSE](LICENSE)。向他人提供 FarArc 二进制时，也必须按 GPLv3 向接收者提供该版本的完整对应源码，包括用于生成该二进制的构建脚本和必要材料，并保留适用的版权和来源声明。

第三方组件继续适用各自许可证。已知组件、内置二进制和审计边界见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

GNU GPL 处理版权许可，但不会自动授予商标、名称、图标、肖像或其他品牌素材的使用权。正式商业运营前应完成独立的品牌、素材、依赖、隐私和出口合规审查。
