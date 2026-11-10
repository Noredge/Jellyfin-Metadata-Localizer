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

## Requirements

- Jellyfin Server **12.0.0** on **Windows x64**, with administrator access to the server dashboard.
- A public HTTPS API compatible with OpenAI Chat Completions, an API key and a suitable model.

The plugin uses one saved connection and model. Node.js, Python and the .NET SDK are needed only for development or the optional offline tools, not for running the installed plugin.

## Installation

1. Download the ZIP and `SHA256SUMS.txt` from [Releases](https://github.com/Noredge/Jellyfin-Metadata-Localizer/releases).
2. Verify the ZIP's SHA-256 against the checksum file. In PowerShell: `Get-FileHash .\Jellyfin.MetadataLocalizer-1.0.0-win-x64.zip -Algorithm SHA256`.
3. Back up and stop Jellyfin. Place the ZIP's complete `Localizer` folder in the server's `plugins` directory, replacing the previous folder when upgrading, then restart.
4. Open **Metadata Localizer** in the Jellyfin server dashboard.

Upgrades keep `data/metadata-localizer-v2`, including translations, settings and credentials. See [Backup and recovery](docs/BACKUP.md) for backup tools and older data layouts.

## First setup and use

1. On first use, create the plugin database and choose managed movie libraries and fields. Disable those libraries' metadata savers to enable display updates.
2. Enter the API address and key → get models → choose a model → save. Model lookup uses the current form values; the translation connection test is optional.
3. Preview and fill existing missing translations. Adding a library establishes its baseline; use batch fill to process its existing backlog.
4. Enable automatic processing when wanted to handle new movies and changed sources.

The overview shows field coverage, recent tasks and issues linked to the affected movies. Open a movie to review its original text, saved translations and current display, edit translations or choose a history version. Shared genre terms and person-name rules are managed in settings.

Available results are automatically displayed for submitted work, and saved translations are reused when switching the target language. Displayed metadata is shared by all Jellyfin users. NFO files are read as sources and are not rewritten.

Generation sends the selected source text and name context to your configured provider. API keys are stored with Windows DPAPI. Optional installation-local prompts and default genre terms are described in [Local rules](docs/LOCAL-RULES.md).

## Build from source

On Windows x64, install Git, the **.NET SDK 10.0.401** pinned in `global.json`, **Node.js 22+**, **Python 3.12+** and PowerShell. Then run:

```powershell
git clone https://github.com/Noredge/Jellyfin-Metadata-Localizer.git
Set-Location Jellyfin-Metadata-Localizer
./scripts/Build.ps1
./scripts/Test.ps1
./scripts/Package.ps1
```

- `Build.ps1` restores locked NuGet dependencies and builds the five plugin assemblies in Release mode.
- `Test.ps1` runs storage, queue, translation, backup and web regression checks with synthetic provider responses. It requires no running Jellyfin server or API key.
- `Package.ps1` builds and verifies the distribution ZIP and `SHA256SUMS.txt` under `artifacts/<version>/`. Install its complete `Localizer` folder as described above.

The scripts accept `-Dotnet`, `-Node` and `-Python` tool paths as applicable. To change the management page, edit `src/Localizer.Plugin/Web/` and regenerate its embedded HTML with `node scripts/Build-Web.mjs --write`.

See [Development](docs/DEVELOPMENT.md) for more on builds, the web page and checks, or [Changelog](CHANGELOG.md) for release notes.

## License

GPL-3.0-only. See [LICENSE](LICENSE) and [NOTICE](NOTICE). Unofficial Jellyfin plugin.
