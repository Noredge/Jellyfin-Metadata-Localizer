# Installation / 安装

Target: Jellyfin 12.0.0, Windows x64, .NET 10. Install manually; no update feed is provided.

Stop Jellyfin and back up its program/plugin, configuration and server data directories first. Keep the previous plugin and its paired data outside the active plugins directory. Extract `Localizer/` from the verified ZIP into the server's plugins directory. Use only the five matching DLLs. Start Jellyfin and verify that the loaded plugin version matches `assemblyVersion` in the downloaded `package-manifest.json`. Open Metadata Localizer from the sidebar (or Dashboard → Plugins).

The public plugin identity remains `12bb696e-b52a-4b98-a4fa-39d32bfca5aa`. Public and personal-fork assemblies must not coexist in one process.

## Existing unified installations

When upgrading from a public unified preview or 1.0 RC, retain `data/metadata-localizer-v2` with settings, encrypted credentials, local rules and history. After the paired backup and restore check, stop Jellyfin, replace all five plugin DLLs together and restart. Do not initialize a new database or configure the connection again. A successful startup and unchanged data do not require a real translation test. See [backup and recovery](BACKUP.md).

## First setup for a new installation

Create the new database, then choose movie libraries, fields and target language. No older database is opened or imported: `data/metadata-localizer-v2` is independent of `data/metadata-localizer`. An older-data notice is informational; keep that older directory and backup. Initialization never requires clearing it.

Connection order: **API base + key → get models → select model → save**. Use a public HTTPS base such as `https://api.example.com/v1`, not the `/chat/completions` URL. No local/private address, redirect or proxy is used. Models retain provider order, without type filtering. An unsuitable model follows normal request error handling. JSON response mode can be disabled when unsupported.

The optional fixed-word translation test requires saved settings and explicit confirmation; it uses the same queue and one model request. It is not required for saving. Keys are encrypted by the Jellyfin process under its Windows account; no separate credential script is required. If the process account changes, save the key again. Do not change the server account just to configure this plugin.

## Sources and display

Disable managed libraries' metadata savers before applying display changes. Overview and genre originals come from verified local NFOs. Folder-level `movie.nfo` requires unambiguous ownership using the host's confirmed multipart relationship. Empty overviews are normal. When a title's original cannot be established, use its source-binding editor to confirm the original and prefix.

Submitted available results display automatically for all users. Automatic new/change processing is initially off; enable it deliberately. The first observation of an existing library establishes its baseline, without translating the backlog. Missing-only fill skips selected translations, empty sources, keep-original/protected fields and active work. Failed missing fields may be retried. Explicit retranslation is separate.

Edit a translation with **Save only** to keep display unchanged, or choose **Save and display**. History selection, keep-original and restore actions use revision/conflict guards. New external edits are preserved. Unknown request outcomes remain paused until explicit retry acknowledgement, even after prompt, person-rule, model or language-epoch changes. Changing the input fingerprint does not clear the earlier request evidence.

Person originals and aliases must identify one rule unambiguously. Conflicting edits are rejected without changing the stored rule. Existing ambiguous rules are preserved but cannot generate or apply an incorrect person mapping; correct them and retry. Latin name matching respects word boundaries, while CJK names can remain adjacent to CJK text.

The interface supports Chinese/English and a narrow layout. Idle unchanged pages reduce request frequency up to about ten seconds; active work refreshes about every two seconds. Hidden pages stop polling, while backend work continues.

中文提示：已有公开统一库或 1.0 RC 升级时，先做完整配套备份和恢复校验，停服更换五个 DLL，保留原 `data/metadata-localizer-v2`；无需重新初始化或重新配置连接。仅全新安装需要创建数据库和配置。其他旧架构数据不会自动导入；兼容统一库可按明确的离线接管流程处理。首次接入已有媒体库只建立基线，从小范围开始。

## Compatible unified stores

For optional personal rules and explicit offline adoption of a compatible unified schema-1 store, see [local rules and adoption](LOCAL-RULES.md). No store is selected or merged automatically. Keep the source, complete backup and old plugin for rollback.
