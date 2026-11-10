# Jellyfin Metadata Localizer

简体中文 | [English](README.md)

翻译并管理 Jellyfin 影片的标题、简介和分类，支持人工编辑、译文历史复用及统一显示语言。

**1.0.0 · Jellyfin 12.0.0 · Windows x64**

## 功能

- 简体中文、英文目标语言及双语管理界面。
- 一个兼容 OpenAI 的 HTTPS API 连接与模型。
- 批量补齐缺译，自动处理新增影片和原文变化。
- 人工编辑、译文历史、共享分类词典及人名规则。
- 持久任务恢复、请求记录和直达对应影片的问题入口。
- 只读 NFO 原文，切换语言时优先复用已有译文。

## 环境要求

- **Windows x64** 上的 Jellyfin Server **12.0.0**，以及服务端管理权限。
- 一个兼容 OpenAI Chat Completions 的公共 HTTPS API、API Key 和合适的模型。

插件保存一个连接及模型。Node.js、Python 和 .NET SDK 仅用于开发或可选的离线工具，运行已安装的插件无需另行安装这些工具。

## 安装

1. 从 [Releases](https://github.com/Noredge/Jellyfin-Metadata-Localizer/releases) 下载 ZIP 和 `SHA256SUMS.txt`。
2. 核对 ZIP 的 SHA-256 与校验文件一致。PowerShell 命令：`Get-FileHash .\Jellyfin.MetadataLocalizer-1.0.0-win-x64.zip -Algorithm SHA256`。
3. 备份并停止 Jellyfin，将 ZIP 内完整的 `Localizer` 文件夹放入服务端的 `plugins` 目录；升级时完整替换原文件夹，然后重启。
4. 在 Jellyfin 服务端管理页面中打开 **Metadata Localizer**。

升级时保留 `data/metadata-localizer-v2`，已有译文、设置和凭据会继续使用。备份工具及旧数据目录说明见[备份与恢复](docs/BACKUP.md)。

## 首次设置与日常使用

1. 首次使用时创建插件数据库，选择管理的影片媒体库及字段；关闭这些媒体库的元数据保存器，以便更新显示。
2. 填写 API 地址和 Key → 获取模型列表 → 选择模型 → 保存。获取列表直接使用当前填写的内容，翻译连接测试可选。
3. 预览并补齐已有影片的缺译。添加媒体库会建立基线，已有影片通过批量补齐处理。
4. 按需开启自动处理，处理后续新增影片及原文变化。

概览展示字段覆盖、最近任务，以及可进入对应影片的问题记录。进入影片可对照原文、已有译文和当前显示，人工编辑译文或选择历史版本。共享分类词典和人名规则在设置中管理。

提交任务后，可用结果会自动显示；切换目标语言时优先复用已有译文。显示结果对所有 Jellyfin 用户生效。NFO 文件仅作为原文读取，不会被改写。

生成时会向所选供应商发送选定原文和人名上下文，API Key 使用 Windows DPAPI 保存。可选的本机提示词和默认分类词条见[本机规则](docs/LOCAL-RULES.md)。

## 从源码构建

在 Windows x64 上安装 Git、`global.json` 固定的 **.NET SDK 10.0.401**、**Node.js 22+**、**Python 3.12+** 和 PowerShell，然后执行：

```powershell
git clone https://github.com/Noredge/Jellyfin-Metadata-Localizer.git
Set-Location Jellyfin-Metadata-Localizer
./scripts/Build.ps1
./scripts/Test.ps1
./scripts/Package.ps1
```

- `Build.ps1` 使用锁定的 NuGet 依赖，以 Release 模式构建五个插件程序集。
- `Test.ps1` 检查存储、队列、翻译、备份及网页行为，使用合成供应商响应，无需运行 Jellyfin 或填写 API Key。
- `Package.ps1` 构建并验证发布 ZIP 和 `SHA256SUMS.txt`，输出至 `artifacts/<version>/`。按上述安装步骤放入完整的 `Localizer` 文件夹。

各脚本按需支持 `-Dotnet`、`-Node`、`-Python` 工具路径参数。修改管理页面时，编辑 `src/Localizer.Plugin/Web/`，再执行 `node scripts/Build-Web.mjs --write` 生成内嵌 HTML。

构建、网页开发和检查的更多说明见[开发文档](docs/DEVELOPMENT.md)，版本变化见[更新记录](CHANGELOG.md)。

## 许可证

GPL-3.0-only。详见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。非 Jellyfin 官方插件。
