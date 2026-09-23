# Jellyfin Metadata Localizer

[English](README.md)

为 Jellyfin 提供电影标题、完整 NFO 简介和类型的翻译管理。

**预览版 0.11.0-preview.1 · 仅 Jellyfin 12.0.0 · Windows x64**

## 功能

- 支持中文、英文翻译及双语管理界面。
- 支持 OpenAI、Groq、自定义 OpenAI 兼容云服务和本机 LM Studio。
- 查看、编辑和应用译文，支持批量处理及符合恢复条件的撤销操作。

## 开始使用

按照[安装说明](docs/INSTALL.md)安装插件、配置服务与模型，再用一部测试影片试译。云服务密钥须在运行 Jellyfin 的同一 Windows 账户下保存。

使用前先备份，应用修改前关闭媒体库的元数据保存器。**应用后的元数据由所有用户共享。** 简介需有本地 NFO，插件不会重写 NFO 文件。

## 文档

[备份与恢复](docs/BACKUP.md) · [隐私说明](docs/PRIVACY.md) · [开发说明](docs/DEVELOPMENT.md) · [验证记录](docs/VALIDATION.md)

## 许可

[GPL-3.0-only](LICENSE) · [第三方依赖说明](NOTICE)。本项目为非官方 Jellyfin 插件。
