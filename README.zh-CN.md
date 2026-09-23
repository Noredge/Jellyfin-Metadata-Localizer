# Jellyfin Metadata Localizer

简体中文 | [English](README.md)

用于翻译和管理 Jellyfin 电影标题、完整 NFO 简介与类型的插件。可以先查看和编辑译文，再应用到媒体库，同时保留原文和操作历史，方便管理中文、英文内容。

**0.11.0-preview.1 · 预览版 · Jellyfin 12.0.0 · Windows x64**

## 功能

- 将电影标题和完整 NFO 简介翻译为简体中文或英文。
- 使用云端翻译服务，或通过 LM Studio 使用本地模型。
- 查看、编辑译文，批量处理影片，并使用已保存的译文切换共享显示语言。
- 管理类型翻译词典和可选的人名映射。
- 保留原文与应用历史，在恢复条件仍满足时撤销修改。
- 管理界面支持中文和英文切换。

## 环境要求

- **Windows x64** 上的 Jellyfin Server **12.0.0**，使用管理员账户操作。
- 电影媒体库；翻译简介需要包含原始简介的本地 NFO。
- 已配置的翻译服务和模型。

不兼容旧版 Jellyfin；其他版本及部署平台尚未验证。

## 快速开始

1. 在 [Releases](https://github.com/Noredge/Jellyfin-Metadata-Localizer/releases/tag/v0.11.0-preview.1) 下载插件 ZIP 和 `SHA256SUMS.txt`，安装前核对校验和。
2. 备份并停止 Jellyfin，将 ZIP 中的 `Localizer` 文件夹放入服务器插件目录。若有旧版插件，先移到插件目录之外再替换。
3. 启动 Jellyfin，进入 **控制台 → 插件 → Metadata Localizer → 设置**，选择服务、模型和语言。
4. 先在小型测试库中确认原文，生成并检查译文，再应用到一部影片，并尝试恢复。

目录位置、密钥配置和首次使用步骤见[安装说明](docs/INSTALL.md)。当前采用手动安装，暂不提供自动更新源。

## 翻译服务

| 服务 | 配置方式 |
| --- | --- |
| OpenAI / Groq | 使用预设地址，配置自己的 API 密钥并选择模型。 |
| OpenAI 兼容云服务 | 自定义 HTTPS 基础地址、模型 ID 和独立密钥，使用 Chat Completions 接口。 |
| LM Studio | 在运行 Jellyfin 的同一台机器上启动服务并加载模型。 |

云服务密钥须在运行 Jellyfin 的同一 Windows 账户下保存。翻译质量和模型兼容性取决于所选服务，建议先试译一部影片，再进行批量处理。

## 使用注意

**标题、简介和类型的修改对所有 Jellyfin 用户生效。** 使用前先备份，应用修改前关闭媒体库的元数据保存器。插件只读取 NFO 作为原文来源，不会重写 NFO 文件。

## 文档

[安装说明](docs/INSTALL.md) · [备份与恢复](docs/BACKUP.md) · [隐私说明](docs/PRIVACY.md) · [开发说明](docs/DEVELOPMENT.md) · [验证记录](docs/VALIDATION.md) · [更新记录](CHANGELOG.md)

## 许可

采用 [GPL-3.0-only](LICENSE)，第三方依赖见 [NOTICE](NOTICE)。本项目为非官方 Jellyfin 插件。
