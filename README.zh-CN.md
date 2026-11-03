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

## 开始使用

1. 从 [Releases](https://github.com/Noredge/Jellyfin-Metadata-Localizer/releases) 下载 ZIP 和 `SHA256SUMS.txt`。
2. 备份并停止 Jellyfin，完整替换插件的 `Localizer` 文件夹，然后重启。
3. 打开 **Metadata Localizer**。首次使用时创建数据库，选择管理的媒体库及字段；关闭这些媒体库的元数据保存器，以便更新显示。
4. 填写 API 地址和 Key → 获取模型列表 → 选择模型 → 保存。翻译连接测试可选。
5. 预览并补齐历史缺译，按需开启自动处理。

升级时保留 `data/metadata-localizer-v2`，已有译文、设置和凭据会继续使用。显示结果对所有 Jellyfin 用户生效。生成时向所选供应商发送原文，API Key 使用 Windows DPAPI 保存。

## 更多

[备份与恢复](docs/BACKUP.md) · [本机规则](docs/LOCAL-RULES.md) · [开发](docs/DEVELOPMENT.md) · [更新记录](CHANGELOG.md)

GPL-3.0-only。详见 [LICENSE](LICENSE) 和 [NOTICE](NOTICE)。非 Jellyfin 官方插件。
