# Installation / 安装

Target: Jellyfin 12.0.0, Windows x64, .NET 10. Install manually; no update feed is provided.

Stop Jellyfin and back up its program/plugin, configuration and server data directories first. Keep the previous plugin and its paired data outside the active plugins directory. Extract `Localizer/` from the verified ZIP into the server's plugins directory. Use only the five matching DLLs. Start Jellyfin and verify plugin version **0.12.0.0** in the current startup log. Open Metadata Localizer from the sidebar (or Dashboard → Plugins).

The public plugin identity remains `12bb696e-b52a-4b98-a4fa-39d32bfca5aa`. Public and personal-fork assemblies must not coexist in one process.

## First setup

Create the new database, then choose movie libraries, fields and target language. No older database is opened or imported: `data/metadata-localizer-v2` is independent of `data/metadata-localizer`. An older-data notice is informational; keep that older directory and backup. Initialization never requires clearing it.

Connection order: **API base + key → get models → select model → save**. Use a public HTTPS base such as `https://api.example.com/v1`, not the `/chat/completions` URL. No local/private address, redirect or proxy is used. Models retain provider order, without type filtering. An unsuitable model follows normal request error handling. JSON response mode can be disabled when unsupported.

The optional fixed-word translation test requires saved settings and explicit confirmation; it uses the same queue and one model request. It is not required for saving. Keys are encrypted by the Jellyfin process under its Windows account; no separate credential script is required. If the process account changes, save the key again. Do not change the server account just to configure this plugin.

## Sources and display

Disable managed libraries' metadata savers before applying display changes. Overview and genre originals come from verified local NFOs. Folder-level `movie.nfo` requires unambiguous ownership using the host's confirmed multipart relationship. Empty overviews are normal. When a title's original cannot be established, use its source-binding editor to confirm the original and prefix.

Submitted available results display automatically for all users. Automatic new/change processing is initially off; enable it deliberately. The first observation of an existing library establishes its baseline, without translating the backlog. Missing-only fill skips selected translations, empty sources, keep-original/protected fields and active work. Failed missing fields may be retried. Explicit retranslation is separate.

Edit a translation with **Save only** to keep display unchanged, or choose **Save and display**. History selection, keep-original and restore actions use revision/conflict guards. New external edits are preserved. Unknown request outcomes remain paused until explicit retry acknowledgement.

The interface supports Chinese/English and a narrow layout. Idle unchanged pages reduce request frequency up to about ten seconds; active work refreshes about every two seconds. Hidden pages stop polling, while backend work continues.

中文提示：这是新架构预览版，旧数据保留但不迁移。请先备份，安装后创建新版库并重新配置连接。任务结果自动显示，首次接入已有库不自动补齐历史；从小库、单片开始。
