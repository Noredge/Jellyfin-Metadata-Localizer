# Jellyfin Metadata Localizer

[简体中文](README.zh-CN.md) | English

A Jellyfin plugin for translating and managing movie titles, full NFO overviews, and genres. Review translations before applying them, keep the original sources, and manage the library in Chinese or English.

**0.11.0-preview.1 · Preview · Jellyfin 12.0.0 · Windows x64**

## Features

- Translate movie titles and complete NFO synopses into Simplified Chinese or English.
- Use cloud translation services or a local model through LM Studio.
- Review and edit translations, process movies in batches, and switch the shared display language using saved translations.
- Manage genre translations and optional person-name mappings.
- Keep source and application history, with recovery when the recorded conditions still match.
- Switch the administration interface between Chinese and English.

## Requirements

- Jellyfin Server **12.0.0** on **Windows x64**, with administrator access.
- A movie library; overview translation requires a local NFO containing the synopsis.
- A configured translation service and model.

Earlier Jellyfin versions are not supported. Other versions and deployment platforms have not been validated.

## Quick start

1. Download the plugin ZIP and `SHA256SUMS.txt` from [Releases](https://github.com/Noredge/Jellyfin-Metadata-Localizer/releases/tag/v0.11.0-preview.1). Verify the checksum before installing.
2. Back up and stop Jellyfin, then install the ZIP's `Localizer` folder into the server's plugin directory. Move any previous plugin version outside that directory before replacing it.
3. Restart Jellyfin and open **Dashboard → Plugins → Metadata Localizer → Settings**. Choose your service, model, and languages.
4. Start with a small test library: confirm the original source, generate and review a translation, then apply it and try restoring it.

See the [installation guide](docs/INSTALL.md) for directory details, credential setup, and first-run instructions. Installation is manual; there is no automatic update feed yet.

## Translation services

| Service | Configuration |
| --- | --- |
| OpenAI / Groq | Built-in endpoints; supply your API key and choose a model. |
| OpenAI-compatible cloud API | Set a custom HTTPS base URL, model ID, and separate API key. Uses Chat Completions. |
| LM Studio | Run and load a model on the same machine as Jellyfin. |

Cloud keys must be saved under the Windows account that runs Jellyfin. Translation quality and model compatibility depend on the selected service, so test one movie before starting a batch.

## Before applying changes

**Titles, overviews, and genres are shared by all Jellyfin users.** Back up first and disable the library's metadata savers before applying changes. The plugin reads NFO files as sources and does not rewrite them.

## Documentation

[Installation](docs/INSTALL.md) · [Backup & recovery](docs/BACKUP.md) · [Privacy](docs/PRIVACY.md) · [Development](docs/DEVELOPMENT.md) · [Validation](docs/VALIDATION.md) · [Changelog](CHANGELOG.md)

## License

[GPL-3.0-only](LICENSE). See [NOTICE](NOTICE) for third-party dependencies. This is an unofficial Jellyfin plugin.
