# Development

Build with .NET SDK 10.0.401, Node.js 22+, Python 3.12+ and PowerShell on Windows x64. Jellyfin 12 compile dependencies are locked in NuGet; no server installation is needed for the automated suite.

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
./scripts/Package.ps1
```

Each script accepts tool paths through `-Dotnet`, `-Node` and `-Python` as applicable. Test and build outputs stay in ignored directories. The package contains five Localizer DLLs, documentation and offline data tools.

## Web page

Edit `src/Localizer.Plugin/Web/page.html`, `styles.css` and `scripts/`, then regenerate the embedded resource:

```powershell
node scripts/Build-Web.mjs --write
```

Builds verify that the generated page matches its sources. Direct `dotnet build` accepts `-p:NodeExecutable=<path>`. Jellyfin loads one embedded page and needs no Node installation.

## Checks and releases

The regression suite covers storage, settings, queue/executor recovery, source and display revisions, provider request/response parsing, backup/restore and web behavior. Provider responses are synthetic; the suite sends no real translation requests.

GitHub Actions builds, tests and packages each commit. Release the verified CI artifact and its checksum. Product versions are defined in `Directory.Build.props`. Full-host/browser acceptance and live-provider or long-running checks are recorded separately from automated results.
