# Development

Target: Windows x64 / Jellyfin 12.0.0 / .NET 10. SDK 10.0.401 is pinned in `global.json`. Use Node.js 22+, Python 3.12+ and PowerShell. Compile contracts come from locked official NuGet packages; an installed Jellyfin directory is not required to build or run synthetic regression checks.

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
./scripts/Package.ps1
```

Pass `-Dotnet`, `-Node` and `-Python` when tools are not on PATH. Run under a normal Windows account for synthetic DPAPI checks. Console check programs are used instead of `dotnet test`. Test artifacts stay in ignored `work/`; build output and release ZIPs are ignored. The package contains five Localizer DLLs, public docs, license/notices and the offline backup helper. Host runtime libraries are not bundled.

`Test.ps1` runs storage, settings, executor/race/process-recovery, read-model, transport, frontend and public setup/backup/privacy checks. Provider traffic is injected or intercepted, never real. Public first-install checks verify a separate data root with a locked old-store sentinel and automatic processing off.

Preserve single-executor request accounting, immutable evidence, source and revision checks, endpoint-key binding, FULL write transactions and commit-boundary wakeup. Pooled read-only connections are separate from writable ones; retire their pool before offline replacement. NFO snapshots last only one media read; send/write boundaries read fresh sources.

GitHub Actions builds/tests/packages the exact commit. Before publishing, download its artifact, verify manifest/inventory, DLL identity/version and SHA-256, and test the exact package on an isolated Jellyfin host and actual browser. Do not treat source checks as deployment or device acceptance. Never use production media or keys in a test fixture.
