# Jellyfin Metadata Localizer

[简体中文](README.zh-CN.md) | English

Translate and manage movie titles, NFO synopses, and genres in Jellyfin. Keep original sources and translation history, reuse saved translations, and follow one shared display language.

**0.12.0-preview.1 · Preview · Jellyfin 12.0.0 · Windows x64**

## Features

- One plugin database and a durable background queue for generation, saving, and display updates.
- Simplified Chinese and English targets, bilingual administration, editable translations and history.
- One public HTTPS OpenAI-compatible Chat Completions connection and model.
- Missing-only batch fill, shared genre dictionary, optional person-name rules, and actionable issue links.
- Idle polling backoff, paged lists, and protected reading/confirmation state during refresh.
- Source, revision, external-edit and metadata-saver checks; NFO files are read, never rewritten.

## Requirements

Jellyfin Server **12.0.0**, Windows x64, administrator access, and a public HTTPS OpenAI-compatible API. Other hosts and platforms are unvalidated. Local/private endpoints, LM Studio native APIs, and multiple saved service profiles are not supported in this version.

## Install and start

1. Download the ZIP and `SHA256SUMS.txt` from [Releases](https://github.com/Noredge/Jellyfin-Metadata-Localizer/releases/tag/v0.12.0-preview.1). Verify the checksum.
2. Back up and stop Jellyfin. Move the previous plugin folder outside the plugin directory; install the ZIP's `Localizer` folder and restart. Do not mix public and personal-fork DLLs.
3. Open **Metadata Localizer**, create the new database, and select managed movie libraries and fields.
4. Enter the API base and key → get models → select a model → save. Model lookup uses the draft without requiring a prior save or model. The translation test is optional and consumes one request if confirmed.
5. Disable the managed library's metadata savers, review the sources, and start with a small library. Empty synopses are skipped normally.

**Available results are automatically displayed for submitted work. Metadata is shared by all Jellyfin users.** Automatic processing starts disabled; enabling it watches new or changed movies. Adding an existing library first establishes a baseline; explicitly use missing-only fill for its backlog. See [Installation](docs/INSTALL.md).

## Earlier versions

This is a new implementation based on the unified architecture. It uses `data/metadata-localizer-v2`; the old `data/metadata-localizer` directory stays untouched. Old settings, credentials, translations and jobs are not imported. The old release and Git history remain available. Keep a complete paired backup before switching; initialize and configure this version separately. See [Backup and recovery](docs/BACKUP.md).

## Connection and privacy

The server stores the key using Windows CurrentUser DPAPI, bound to the endpoint. A blank key keeps the saved key for the same address; a new address needs a key. The page never reads the key back. Provider/model capabilities vary; JSON mode and output budgets are configurable. Selected original text and protected-name context are sent to the configured service only when generation is requested. Unknown outcomes require explicit acknowledgement before retrying.

## Documentation

[Installation](docs/INSTALL.md) · [Backup](docs/BACKUP.md) · [Privacy](docs/PRIVACY.md) · [Development](docs/DEVELOPMENT.md) · [Validation](docs/VALIDATION.md) · [Changelog](CHANGELOG.md)

## License

[GPL-3.0-only](LICENSE); see [NOTICE](NOTICE). Unofficial Jellyfin plugin.
