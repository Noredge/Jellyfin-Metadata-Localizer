# Jellyfin Metadata Localizer

[简体中文](README.zh-CN.md)

An administrator tool for translating and managing movie titles, full NFO overviews, and genres in Jellyfin. Review a translation, edit it, and apply it to the shared library display while keeping source records and recovery history.

**0.11.0-preview.1 · Windows x64 · Jellyfin 12.0.0 only · .NET 10**

This is a preview candidate. See [validation](docs/VALIDATION.md) for recorded local evidence and limits; GitHub Actions results are recorded separately for each commit.

## What it does

- Translate movie titles and complete NFO synopses into Simplified Chinese or English.
- Review or edit candidates before applying them; retain source, selection, and application history separately.
- Use OpenAI, Groq, a custom OpenAI-compatible cloud API, or LM Studio running on the same machine, with an explicitly selected model.
- Manage genre translations and optional title display-name mappings.
- Run library tasks, inspect progress and failures, and restore supported field changes when their recorded preconditions still hold.
- Switch the administration menu between Simplified Chinese and English.

Applied titles, overviews, and genres are **shared Jellyfin metadata visible to all users**. This is not a per-user language selector. Only movie items are supported in this preview.

## Before using it

Back up Jellyfin and start with a small test library. Disable the library's metadata savers before applying changes; the plugin refuses writes when it cannot verify this setting. Original NFO files are read as source and are not rewritten by the plugin.

Titles require a confirmed original source. A localized library title that differs from `OriginalTitle` may require manual confirmation in **Source & history**. Confirmation records the source and current display separately without renaming the movie; later external edits still require review. Overviews require an unambiguous local movie NFO with a nonempty `<plot>`; a database synopsis alone is not an overview source. Multipart ownership is checked against Jellyfin's additional-parts relationship.

The preview targets Jellyfin **12.0.0**, with no compatibility layer for earlier versions. Other Jellyfin versions, Linux, containers, and NAS deployments are not covered. Cloud credentials use Windows CurrentUser DPAPI and must be created under the Windows account that runs Jellyfin.

## Get started

1. Read [installation and first run](docs/INSTALL.md).
2. Review [what leaves your machine](docs/PRIVACY.md).
3. Follow [backup and recovery](docs/BACKUP.md).
4. For source builds, use [development instructions](docs/DEVELOPMENT.md).

For a custom cloud provider, add an OpenAI-compatible service in Settings, enter its HTTPS API base URL and model ID, and save the service settings. Then save its separate credential using the included helper and test the model list. The protocol is Chat Completions with Bearer authentication. JSON response mode is optional; vendor-specific reasoning parameters are not sent. A model-list response does not prove that a model accepts translation requests, so start with one movie.

There is no plugin repository feed or automatic update channel in this preview. The ZIP is a manual-install package, not a Jellyfin Server installer.

## License and dependencies

Proposed public license: [GPL-3.0-only](LICENSE). See [NOTICE](NOTICE) for upstream dependencies. Jellyfin is a separate project; this plugin is not an official Jellyfin component.
