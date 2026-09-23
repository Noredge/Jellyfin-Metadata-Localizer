# Jellyfin Metadata Localizer

[简体中文](README.zh-CN.md)

Translate and manage movie titles, full NFO overviews, and genres in Jellyfin.

**Preview 0.11.0-preview.1 · Jellyfin 12.0.0 only · Windows x64**

## Features

- Chinese and English translations, with bilingual administration menus.
- OpenAI, Groq, custom OpenAI-compatible cloud APIs, and local LM Studio.
- Review, edit, and apply translations; batch processing and supported change recovery.

## Get started

Follow the [installation guide](docs/INSTALL.md) to install the plugin, configure a service and model, and translate a test movie. For cloud services, save credentials under the Windows account that runs Jellyfin.

Back up first and disable the library's metadata savers before applying changes. **Applied metadata is shared by all users.** Overviews require a local NFO; the plugin does not rewrite NFO files.

## Documentation

[Backup & recovery](docs/BACKUP.md) · [Privacy](docs/PRIVACY.md) · [Development](docs/DEVELOPMENT.md) · [Validation](docs/VALIDATION.md)

## License

[GPL-3.0-only](LICENSE) · [Third-party notices](NOTICE). An unofficial Jellyfin plugin.
