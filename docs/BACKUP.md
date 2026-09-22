# 备份与恢复 / Backup and recovery

应用翻译会改变所有用户看到的标题、简介或类型。插件中的字段恢复和完整服务器恢复是两种不同操作。

## 完整备份

停止 Jellyfin 并确认没有相关进程继续写入，再备份：

- 对应版本的 Jellyfin 程序目录、启动参数或服务配置。
- 完整 Jellyfin 配置和数据目录，包括数据库及 `-wal`、`-shm`、`-journal` 等旁文件。
- 插件目录，以及 `DataPath/metadata-localizer` 整个目录。

完整插件数据包括来源绑定、候选、应用和恢复记录、任务进度、词典、服务设置与加密凭据。不要只复制单个 `.db`。备份内可能含原文、文件路径和加密密钥，应私下保管，不能上传到公开 issue 或仓库。

## 离线插件备份工具

需要 Python 3.12+。工具仅处理插件数据，**不备份 Jellyfin 主数据库、程序或媒体文件**。`--server-stopped` 是你的人工确认，工具不会替你检测或停止服务器。

```powershell
python .\tools\Localizer-DataBackup.py backup '<插件数据根目录>' '<备份目录>\localizer-backup.zip' --server-stopped
python .\tools\Localizer-DataBackup.py restore '<备份目录>\localizer-backup.zip' '<全新恢复目录>' --server-stopped
```

源码中的工具位于 `scripts/`。工具验证文件哈希和数据库完整性，保存旁文件；备份不覆盖已有 ZIP，恢复只创建新目录，不覆盖安装目录。上限为 10,000 个文件、总计 1 GiB。尚未初始化 `candidates.db` 的空安装无需使用此工具；直接保留配置并备份服务器即可。

恢复后先检查新目录，再在 Jellyfin 停止状态下由管理员替换安装数据，保留替换前的完整目录。涉及共享显示时，必须配合时间对应的 Jellyfin 主数据库快照；仅恢复插件数据库不会撤销主库里的字段。

## 字段恢复

使用页面的应用与恢复历史可以恢复符合条件的已记录字段。它会检查当前值、来源和操作状态；条目被外部刷新或人工修改后，恢复可能被拒绝。此时先检查差异，不要通过直接改数据库强行覆盖。

## 回退插件或服务器

停止服务器后，恢复成套的程序、插件、配置和数据快照，再启动并检查日志与抽样条目。不要把旧 DLL 直接覆盖到已被新版本迁移的数据上，也不要在服务器运行时替换数据库。DPAPI 凭据通常需要原 Windows 账户；跨机器或换账户后应重新保存密钥。

English summary: take an offline, matching server/program/data snapshot. The included helper only backs up Localizer data and restores into a new directory. It preserves database sidecars and verifies hashes and integrity. Restoring plugin data alone does not restore Jellyfin's shared metadata.
