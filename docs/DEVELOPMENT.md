# Development

Target: Windows x64 / Jellyfin 12.0.0 / .NET 10. SDK 10.0.401 is pinned in `global.json`. Use Node.js 22+, Python 3.12+ and PowerShell. Compile contracts come from locked official NuGet packages; an installed Jellyfin directory is not required to build or run synthetic regression checks.

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
./scripts/Package.ps1
```

Pass `-Dotnet`, `-Node` and `-Python` when tools are not on PATH. Run under a normal Windows account for synthetic DPAPI checks. Console check programs are used instead of `dotnet test`. Test artifacts stay in ignored `work/`; build output and release ZIPs are ignored. The package contains five Localizer DLLs, public docs, license/notices and the offline backup helper. Host runtime libraries are not bundled.

## Embedded web page

Edit `src/Localizer.Plugin/Web/page.html`, `styles.css` and the files under `Web/scripts/`.
The script files group API/read helpers, overview, movies/editor, settings, work, rules and lifecycle functions. Page state and event registration have their own files. Error codes keep Chinese and English text together in `i18n.mjs`.

After editing these sources, run `node scripts/Build-Web.mjs --write` and include the generated `Web/unified.html` in the same change. The dependency-free assembler removes authoring exports and joins the scripts inside one page closure. Jellyfin still loads one embedded resource; no additional assets or Node installation are needed on the server.

Every plugin build checks that the generated page matches its sources. Direct `dotnet build` therefore needs Node on PATH, or `-p:NodeExecutable=<absolute-node-path>`. Build/package scripts accept `-Node`. Tests import named authoring exports and evaluate their complete function bodies, so source formatting does not change the test boundary. The full assembled closure is tested separately.

`Test.ps1` runs storage, settings, executor/race/process-recovery, read-model, transport, frontend and public setup/backup/privacy checks. Provider traffic is injected or intercepted, never real. Public first-install checks verify a separate data root with a locked old-store sentinel and automatic processing off.

Preserve single-executor request accounting, immutable evidence, source and revision checks, endpoint-key binding, FULL write transactions and commit-boundary wakeup. Pooled read-only connections are separate from writable ones; retire their pool before offline replacement. NFO snapshots last only one media read; send/write boundaries read fresh sources.

GitHub Actions builds/tests/packages the exact commit. Before publishing, download its artifact, verify manifest/inventory, DLL identity/version and SHA-256, and test the exact package on an isolated Jellyfin host and actual browser. Do not treat source checks as deployment or device acceptance. Never use production media or keys in a test fixture.

## One maintenance branch

Maintain runtime code, tests and release scripts here. Product versions are defined only in `Directory.Build.props`; `Package.ps1` and `Verify-Package.py` read that file. Do not copy runtime fixes back into a personal fork. Installation-specific rules belong in `local-rules.json` outside the repository, using [local rules](LOCAL-RULES.md).
