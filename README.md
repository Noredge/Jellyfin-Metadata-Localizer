# Jellyfin Metadata Localizer

[简体中文](README.zh-CN.md) | English

Translate movie titles, synopses and genres in Jellyfin, with editable translations, reusable history and a shared display language.

**1.0.0 · Jellyfin 12.0.0 · Windows x64**

## Features

- Simplified Chinese and English targets with a bilingual management page.
- One OpenAI-compatible HTTPS API connection and model.
- Missing-only batch fill and automatic processing of new or changed movies.
- Manual editing, translation history, shared genre dictionary and person-name rules.
- Durable task recovery, request records and issue links to the affected movie.
- Read-only NFO sources; saved translations are reused across language switches.

## Quick start

1. Download the ZIP and `SHA256SUMS.txt` from [Releases](https://github.com/Noredge/Jellyfin-Metadata-Localizer/releases).
2. Back up and stop Jellyfin. Replace the plugin's complete `Localizer` folder, then restart.
3. Open **Metadata Localizer**. On first use, create its database and choose managed libraries and fields. Disable those libraries' metadata savers to enable display updates.
4. Enter the API address and key → get models → choose a model → save. The connection translation test is optional.
5. Preview and fill existing missing translations. Enable automatic processing when wanted.

Upgrades keep `data/metadata-localizer-v2`, including translations, settings and credentials. Displayed metadata is shared by all Jellyfin users. Generated text is sent to the configured provider; API keys are stored with Windows DPAPI.

## More

[Backup and recovery](docs/BACKUP.md) · [Local rules](docs/LOCAL-RULES.md) · [Development](docs/DEVELOPMENT.md) · [Changelog](CHANGELOG.md)

GPL-3.0-only. See [LICENSE](LICENSE) and [NOTICE](NOTICE). Unofficial Jellyfin plugin.
