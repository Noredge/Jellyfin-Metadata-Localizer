# Jellyfin Metadata Localizer

简体中文 | [English](README.md)

为 Jellyfin 翻译和管理电影标题、NFO 简介与分类。保留原文和译文历史，优先复用已有译文，并按统一目标语言显示。

**0.12.0-preview.1 · 预览版 · Jellyfin 12.0.0 · Windows x64**

## 功能

- 一个插件数据库、一套持久队列，统一生成、保存和更新显示。
- 简体中文/英文目标与双语管理界面，可编辑译文并选用历史版本。
- 单个公共 HTTPS OpenAI-compatible Chat Completions 连接及模型。
- 仅补缺译、共享分类词典、人名规则分页与问题定位。
- 空闲退避、分页查询，刷新时保留阅读位置和未知结果确认。
- 校验原文、修订、外部修改及元数据保存器；只读 NFO，不写回。

## 环境要求

Jellyfin Server **12.0.0**、Windows x64、管理员权限及公共 HTTPS 兼容 API。其他平台尚未验证。本版不支持本机/内网地址、LM Studio 原生接口或多个服务配置。

## 安装与开始

1. 从 [Releases](https://github.com/Noredge/Jellyfin-Metadata-Localizer/releases/tag/v0.12.0-preview.1) 下载 ZIP 和 `SHA256SUMS.txt`，先校验文件。
2. 完整备份并停止 Jellyfin。将旧插件目录移出插件目录，安装 ZIP 中的 `Localizer` 文件夹后重启；不要混装公开版和个人分支 DLL。
3. 打开 **Metadata Localizer**，创建新版数据库，选择管理的电影库与字段。
4. 填写 API 地址和 Key → 获取模型列表 → 选择模型 → 保存。获取列表使用当前草稿，无需先保存或预填模型。翻译测试可选，确认执行会消耗一次请求。
5. 关闭管理库的元数据保存器，核对原文，从小范围开始。空简介正常跳过。

**已提交任务的可用结果会自动显示，影片元数据由所有 Jellyfin 用户共享。** 自动处理初始关闭，开启后观察新增及变化的影片。首次接入已有库只建立基线，历史缺译由“补齐管理范围”明确提交。详见[安装说明](docs/INSTALL.md)。

## 旧版用户

本版采用统一架构，数据位于 `data/metadata-localizer-v2`。旧 `data/metadata-localizer` 目录原样保留，不导入旧设置、密钥、译文和任务。旧 Release 与 Git 历史继续保留。切换前保留完整配套备份，新版单独初始化和配置，详见[备份与恢复](docs/BACKUP.md)。

## 连接与隐私

密钥在服务器运行账户下通过 Windows CurrentUser DPAPI 加密并绑定地址，页面不读回。相同地址留空可保留已保存密钥；换地址需要密钥。模型兼容能力因供应商而异，JSON 模式及输出上限可配置。执行生成时会向所选服务发送对应原文及受保护人名上下文。请求结果未知时不会静默重发，需明确接受可能重复计费。

## 文档与许可

[安装](docs/INSTALL.md) · [备份](docs/BACKUP.md) · [隐私](docs/PRIVACY.md) · [开发](docs/DEVELOPMENT.md) · [验证](docs/VALIDATION.md) · [更新记录](CHANGELOG.md)

[GPL-3.0-only](LICENSE)，第三方说明见 [NOTICE](NOTICE)。非 Jellyfin 官方插件。
