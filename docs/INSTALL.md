# 安装与首次使用 / Installation

## 环境

- Windows x64，Jellyfin Server **12.0.0**，使用管理员账户打开 Jellyfin Web。
- 安装包只包含插件；不负责升级 Jellyfin Server。
- 手动安装不需要 .NET SDK、Node.js 或 Python。备份辅助工具另需 Python 3.12+；源码构建见 [DEVELOPMENT.md](DEVELOPMENT.md)。
- 当前版本未验证 Jellyfin 12.1 或其他版本；不支持旧版兼容安装。

## 安装步骤

1. 在 Jellyfin 控制台或启动日志中确认程序版本以及实际插件目录、数据目录。自定义 `--datadir` 或服务配置会改变目录，不要仅凭默认路径操作。
2. 停止 Jellyfin，确认进程退出；按 [BACKUP.md](BACKUP.md)备份程序、配置和数据。
3. 解压候选 ZIP，核对同目录 `SHA256SUMS.txt` 中的文件名和 SHA-256。PowerShell 可使用 `Get-FileHash -Algorithm SHA256 <ZIP文件>`。
4. 将 ZIP 中整个 `Localizer` 文件夹放入实际插件目录。该目录应包含四个 `Localizer.*.dll`；不要额外复制 Jellyfin、SQLite 或 ASP.NET DLL。
5. 若已安装同一插件，将旧 `Localizer` 文件夹移到插件目录**之外**备份后再替换。两个版本共享插件 ID，不能同时加载。保留原来的插件数据目录。
6. 启动 Jellyfin，查看本次启动日志是否加载 `Metadata Localizer` **0.11.0.0**，并确认没有程序集加载错误。
7. 在管理员控制台进入「插件 → Metadata Localizer → 设置」（英文界面为 `Plugins → Metadata Localizer → Settings`）。可用「已安装 / Installed」筛选找到插件。必要时刷新页面清除旧页面缓存。

本候选 ZIP 不含密钥、私人媒体信息、旧运行数据、插件订阅源或自动更新设置。

## 首次配置

1. 准备一个小型电影测试媒体库，关闭该媒体库的**所有元数据保存器**。应用翻译前插件会检查这个条件。
2. 在插件的设置页面选择菜单语言、目标语言和服务。初始模型显示「请配置模型」，不会自动选择可能收费的模型。
3. 对于 LM Studio，在 Jellyfin 所在机器启动服务并加载模型。仅支持回环地址，例如 `http://127.0.0.1:1234/api/v1`；不接受远程服务器或任意兼容 API 地址。
4. 对于 OpenAI/Groq，使用下方方法保存密钥。
5. 点击连接测试以读取模型列表，选择合适模型并保存。收到列表不代表密钥对翻译接口有效或该模型支持翻译请求；仍需先做一次小范围翻译。

OpenAI、Groq 使用预设端点，LM Studio 使用本机原生 `/api/v1` 接口。其他云服务可按下面的方法添加通用 OpenAI 兼容配置。模型需要按提示返回 JSON；可见的全部模型不一定都能完成翻译。

## 自定义 OpenAI 兼容云服务

1. 在设置中新增自定义服务，填写显示名称、API 基础地址和模型 ID。基础地址示例为 `https://api.example.com/v1`，应使用厂商文档提供的实际地址；不要填写完整的 `/chat/completions` 请求地址，也不要把密钥放在 URL 中。
2. 保存设置，记下该配置的服务 ID。每个自定义服务使用自己的密钥文件，不借用 OpenAI 或 Groq 的密钥。
3. 在运行 Jellyfin 的同一 Windows 账户下保存密钥，随后刷新设置页面：

```powershell
.\tools\Save-Credential.ps1 -Provider openai-compatible -ServiceId '<设置中显示的服务ID>' -PluginDataPath '<实际插件数据根目录>'
```

4. 点击连接测试以读取模型列表；也可以直接填写厂商提供的模型 ID。部分兼容服务不提供 `/models`，模型列表测试失败不等于翻译接口不可用。用一部测试影片验证实际翻译，再扩大范围。

通用接口为 `GET <基础地址>/models` 和 `POST <基础地址>/chat/completions`，使用 `Authorization: Bearer` 鉴权。翻译请求使用 `model`、`messages`、`max_tokens` 等通用字段。默认不发送 `reasoning_effort`、`store` 等厂商专属参数；仅在服务支持时勾选 JSON 输出模式发送 `response_format`。此模式不支持 Responses、Anthropic 或 Gemini 的原生协议。

仅接受公开网络的 HTTPS 端点，使用直接连接而非系统 HTTP 代理，不跟随重定向，也不访问回环、局域网或链路本地地址。本机模型继续使用 LM Studio 配置。已有密钥的自定义服务不能改为另一个 API 地址；更换厂商时新增服务并保存对应密钥。删除配置不会删除密钥文件或改写已有任务的服务快照。

## 云服务密钥

插件数据根目录为 Jellyfin `IApplicationPaths.DataPath` 下的 `metadata-localizer`。常见 Windows 安装中是 `C:\ProgramData\Jellyfin\Server\data\metadata-localizer`，请以实际运行配置为准。

在**运行 Jellyfin 的同一个 Windows 账户**下执行 PowerShell：

```powershell
# 在解压目录执行。将路径替换为已确认的实际插件数据根目录。
.\tools\Save-Credential.ps1 -Provider openai -PluginDataPath 'C:\ProgramData\Jellyfin\Server\data\metadata-localizer'
# Groq 则使用 -Provider groq。源码目录中脚本位于 scripts，而非 tools。
```

输入隐藏，不接受命令行明文密钥，不发起网络请求。默认不覆盖已有密钥；确需更换时加 `-Replace`。预设服务的加密文件位于 `credentials/openai-api-key.dpapi` 或 `credentials/groq-api-key.dpapi`；自定义服务位于 `credentials/service-<小写服务ID>-api-key.dpapi`。

DPAPI 绑定 Windows 账户。以管理员打开 PowerShell并不保证账户正确；如果 Jellyfin 由另一个服务账户运行，当前交互账户保存的密钥通常无法读取。不要为了此插件随意改变 Jellyfin 服务身份。无法在正确账户下配置密钥时，可以先使用本机 LM Studio。「已找到密钥文件」、已选择模型、已读取模型列表是不同状态，均不能单独证明翻译已成功。

## 首次翻译与应用

1. 选择测试媒体库，先检查一部电影的原文和目标语言。
2. 标题来源需要确认。自动确认仅适用于现有 `Name` 以 `OriginalTitle` 结尾、前缀较短的条目；其他条目到「来源与历史」核对后手动确认。例如当前显示「飞屋环游记」、原始标题为 `Up`，确认后会保留这两个不同值，允许正常翻译，不需要先把电影改回英文名。后续外部修改仍触发冲突检查；已有应用和恢复记录优先决定预期显示，重新确认不能消除这些冲突。
3. 简介来源为本地 `<movie><plot>` NFO（上限 24,000 字符）。多段视频必须由 Jellyfin 确认属于同一条目；归属不明的 NFO 会被拒绝。
4. 生成候选，检查片名、人名和简介完整性，编辑必要内容，再执行批准和应用。
5. 在 Jellyfin 电影详情页确认结果，并测试一次恢复。然后再扩大任务范围。

默认保留人名原文。标题可使用人名显示词典；简介的人名保护仍以来源人名为准。未识别的类型保留原值，可在词典中补充。

## 常见提示

| 提示 | 处理 |
| --- | --- |
| 请配置模型 | 在设置中测试连接、选择模型并保存。 |
| 原始标题需要检查 | 在来源与历史中核对原文和前缀。 |
| 无法使用 NFO / 归属不明确 | 检查条目路径、同目录 NFO 和 Jellyfin 附加部分关系；不要强行指定不属于该电影的 NFO。 |
| 元数据保存器未关闭 | 修改该媒体库设置后重新检查。 |
| 来源或显示已变化 | 重新读取条目，核对期间的手动编辑或媒体库刷新。 |
| 无法读取服务密钥 | 核对密钥文件位置、运行账户和权限，再测试连接。 |

English summary: stop and back up Jellyfin, copy `Localizer/` into the actual plugins directory, restart, verify version 0.11.0.0 in the current startup log, then configure a provider and model. Use a small movie library with metadata savers disabled. Cloud credentials must be saved under the same Windows account as Jellyfin. Review and confirm sources before applying shared metadata.
