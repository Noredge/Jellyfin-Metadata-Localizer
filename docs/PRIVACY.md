# 数据与隐私 / Data and privacy

## 本地保存

插件使用 Jellyfin 数据目录下的独立 `metadata-localizer` 文件夹，保存来源快照、候选、应用记录、任务、词典、设置及 DPAPI 密钥文件。记录可能包含媒体 ID、原始文本、人物名称和本地媒体路径。公开版本不附带任何现有媒体库、个人词典、密钥或运行历史。

## 翻译服务

管理员发起翻译时，插件向所选服务发送完成该请求所需的标题、简介或类型文本、来源人名以及翻译规则。完整 NFO 简介翻译会发送 `<plot>` 的全部文本；其中若含个人信息也会一起发送。模型列表测试会连接所选服务，但不翻译电影条目。

OpenAI 和 Groq 使用固定的官方 HTTPS 端点。自定义 OpenAI 兼容服务会将上述内容和该服务独立的 Bearer 密钥发送到管理员填写的 HTTPS 地址，请核对地址属于预期服务商；插件拒绝私有网络目标和重定向。LM Studio 仅接受本机回环地址；其模型下载和 LM Studio 自身行为由该软件决定。插件不会上传媒体视频文件，也不需要把个人媒体库发布到 GitHub。

本项目没有自己的遥测服务。服务提供方的费用、保留政策和模型能力由对应提供方决定；预设请求中的 `store=false` 不能等同于对第三方所有数据保留行为的保证，自定义兼容请求默认不发送这个厂商选项。开始批量任务前，先确认所选服务和模型适合你的内容。

## 凭据和排错

云服务密钥通过 Windows CurrentUser DPAPI 加密，不通过插件 Web 设置接口提交。不要将密钥、加密密钥文件、整个数据备份或真实 NFO 提交到仓库。报告问题时使用虚构电影条目，隐藏媒体路径、访问令牌和服务响应中的个人信息。

English summary: selected source text and names are sent to the configured translation provider when a translation is requested. Full overview translation sends the complete NFO plot. Local records may contain private metadata and paths. Keep backups and credentials private and use synthetic examples in issue reports.
