# Development

The supported build target is Windows x64 / Jellyfin 12.0.0 / .NET 10. Build references come from pinned official NuGet packages; no installed Jellyfin program directory is used as a compiler dependency.

## Tools

- .NET SDK 10.0.400, selected by `global.json`.
- PowerShell 5.1+.
- Node.js 22+ for browser-script regression checks.
- Python 3.12+ for backup and package checks.

From the repository root:

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Package.ps1
```

`Test.ps1` runs eight .NET functional check programs, all `Test-*.cjs` checks, backup/restore checks, synthetic Windows DPAPI credential checks, and the source privacy guard. Add `-IncludePerformance` to run the separate synthetic storage benchmark as a ninth .NET group. It does not start Jellyfin or contact a translation provider. Run it under a normal Windows user account: a restricted process that cannot use CurrentUser DPAPI cannot pass the credential check.

If tools are not on PATH, supply `-Dotnet`, `-Node`, and `-Python` to the scripts that use them. For example:

```powershell
.\scripts\Test.ps1 -Dotnet 'C:\Tools\dotnet\dotnet.exe' -Node 'C:\Tools\node\node.exe' -Python 'C:\Tools\python\python.exe'
```

The C# checks are console programs invoked by the test script, not a `dotnet test` suite. Dependency lock files are checked in and restores use locked mode. An intentional package upgrade requires updating and reviewing those lock files.

Build output is under `src/*/bin/`; local work and packages are ignored by Git. The test summary is `work/test-results.json`, with individual logs and reports under `work/checks/`. Package output is `artifacts/0.11.0-preview.1/Jellyfin.MetadataLocalizer-0.11.0-preview.1-win-x64.zip`, alongside `SHA256SUMS.txt`. The package verification summary is `work/package-results.json`.

The package contains only the four plugin assemblies, public documentation, and operational helpers under `tools/`. Jellyfin-provided runtime dependencies are not bundled. The manifest records per-file hashes, the publishable source-tree hash, assembly versions, and hashes of the locked NuGet compile references. These reference hashes describe build inputs, not a particular installed server. ZIP entry timestamps and ordering are fixed. Re-running packaging with unchanged source files, build inputs, and toolchain produces the same archive.

To check an existing archive without rebuilding:

```powershell
python .\scripts\Verify-Package.py --verify .\artifacts\0.11.0-preview.1\Jellyfin.MetadataLocalizer-0.11.0-preview.1-win-x64.zip
python .\scripts\Privacy-Check.py
```

The privacy guard checks for common credential and local-path leaks and rejects unexpected files. Review the public diff as well; passing pattern checks does not prove that all sensitive information has been removed.

The GitHub Actions workflow checks repository pushes and pull requests. A successful local build is not a CI result or proof that the plugin loads in a live server. Before release, verify the exact CI artifact, then test a clean Jellyfin 12.0.0 host, both menu languages, a narrow layout, and a reversible synthetic metadata change.

Keep sources, translation candidates, and field applications distinct. Preserve NFO ownership checks, revision/conflict checks, explicit metadata-saver checks, and recovery preconditions. Do not use private media or credentials in tests. Do not directly mutate Jellyfin's database.
