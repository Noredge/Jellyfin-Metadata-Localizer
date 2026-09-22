# Validation status

Version: **0.11.0-preview.1** (assembly **0.11.0.0**).

This document records local validation of the preview candidate. It is not a release announcement or a claim that a particular GitHub Actions run succeeded.

## Local results

Validated on Windows on 2026-09-21:

- Release build against locked official Jellyfin 12.0.0 NuGet packages: **0 warnings, 0 errors**; all four Localizer assemblies report **0.11.0.0**.
- **35 automated check groups passed**, including nine .NET groups (426 checks, with the separate storage benchmark), 23 browser-script groups, backup/restore (23 checks), credential setup (21 checks), and source privacy checks.
- Translation checks include short, numeric, and accented movie titles, name boundaries, all provider types refusing unconfigured models, and custom Chat Completions request contracts. Custom title, overview, and genre tasks preserve their selected service, endpoint, model, and JSON mode.
- Custom-service checks cover separate credential files, case-insensitive ID collisions, endpoint binding across removal/recreation, public HTTPS validation, and rejection of unsafe DNS answers. No real provider is called by these automated groups.
- Chinese menu logic checks cover 585 static messages and 30 dynamic templates, with language round trips and preservation of input data. These are script checks, not rendered-browser checks.
- A full workspace-mount regression executes initialization and navigation. It reproduces and guards against an undefined-variable startup failure found during the real-host browser check.
- An independent review checked public scope, credentials, documentation, and private-data exclusion. Ambiguous Windows paths are rejected by the credential helper.
- **27 isolated Jellyfin 12.0.0 host checks passed** using a fresh data directory and two synthetic movies. The real host loaded plugin 0.11.0.0, saved a custom service, enforced credential/endpoint isolation, and scanned the library. It confirmed a localized display against a different original title, applied/restored the title, and blocked an external edit. It also confirmed multipart ownership, applied/restored an NFO overview, preserved all NFO bytes, served the Chinese menu, and required administrator authentication.
- Actual Jellyfin Web checks covered initialization, Chinese/English menus, custom service add/save/remove, persisted JSON mode, read-only saved service IDs, the movie list, and the multipart overview editor. Desktop and a 360-pixel viewport were inspected; the checked narrow forms and editor had no horizontal overflow. No new browser errors appeared during the corrected-page check. The isolated servers were stopped afterward.

The host checks made no translation-model requests and did not read or modify production library data. The checked assembly hashes are recorded in the local host report and can be compared with the package manifest. The sandboxed host logged access errors for ASP.NET's user-key directory and the online plugin catalog; the plugin API checks above passed, but this is not a claim of an error-free host environment or cloud-credential acceptance.

A separate five-request cloud smoke test used the existing OpenAI configuration with synthetic text only: short/numeric titles, a protected person name, a full English-to-Chinese synopsis, and an English synopsis preservation case. The sample preserved the checked names, numbers, negation, paragraph structure, and extra feature information. No real library text was sent, no key was copied, and production metadata/settings were not changed. This small sample is not a quality benchmark or a validation of third-party compatible services.

The package script enforces an exact file allowlist, assembly versions, payload hashes, ZIP integrity, source-tree hash, and outer SHA-256 checksum. Local reports are generated under ignored `work/` paths and are not published as private machine logs.

## Remaining acceptance

- The local synthetic offline preview remains a user review aid. Its `file://` URL was unavailable to automation; the rendered checks above used the actual isolated Jellyfin Web application. They do not cover every browser, screen size, or assistive technology.
- A user-selected third-party OpenAI-compatible service still needs a small real translation test with its own credential. Offline protocol tests do not prove support for every model, endpoint, or optional JSON mode. Billing and provider retention policies were not evaluated.
- Verify the GitHub Actions run and exact artifact for the intended commit before releasing. Local results do not establish that a later CI run succeeded.

The automated checks cover core state and conflict rules, translation contracts, Jellyfin integration contracts, browser-script behavior, backup/restore, and package inventory. They do not establish translation quality for every provider/model or replace a real-host acceptance test.

## User review

- Read the scope and installation limits in both READMEs.
- Inspect service setup with no configured model, then choose a model in an isolated test environment.
- Check Chinese and English menus on desktop and at a narrow width.
- Confirm a normal movie title source; inspect short and numeric title behavior.
- Translate and review a synthetic NFO overview, including a multipart movie whose parts are registered with Jellyfin.
- Apply and restore one field; compare the NFO bytes and confirm the shared nature of the change.
- Confirm package inventory, checksum, and the proposed GPL-3.0-only license before publication.

The isolated-host result does not claim that a production installation has loaded this version. Production installation remains a separate operator action.
