# Changelog

## Unreleased

- Reuse enumerated movies and library settings within one inventory observation. Display-name reads use the host's item lookup; request and write boundaries continue to read fresh sources.
- Load person rules only when a saved version actually needs name processing. Share a lazy rule snapshot within a dictionary-render transaction while retaining immutable text and legacy ambiguity protection.
- Group fill, language and submission reads by movie so their existing bounded read scopes share NFO bytes, XML parsing and host resolution.
- Keep readable page, style and script sources by responsibility, assemble the same single embedded page, pair error translations by code, and replace line-based test extraction with named exports.

## 1.0.0-rc.3

- Store verified person placeholders with new model results and rerender only those positions. Identical display names keep their own identities; ordinary words are never globally replaced. Historical plain text is preserved, with an actionable repair error when changed rules cannot be applied safely.
- Persist late responses to an atomic local outbox before database insertion. Retry storage with bounded backoff and recover on startup without another provider send; retain unknown-request evidence and keep late results history-only.
- Atomically bind committed submissions to their previews, so receipt replay survives preview expiry, cleanup and restart. Keep expiry checks for first submissions and reject command reuse with another preview.
- Preserve the schema definition, existing translations, local rules, credentials and plugin identity. Validation uses synthetic provider responses only.

## 1.0.0-rc.2

- Keep unknown-outcome consent bound to the source and target language across prompt, model, person-rule and epoch changes. An indexed lookup and explicit acknowledgement preserve all earlier request evidence.
- Reject cross-person original/alias conflicts transactionally, and block ambiguous legacy rules before sending or applying a cached person mapping. No stored rules or translations are silently rewritten.
- Share Latin word-boundary matching across protection, context selection and cached rendering, including accented/combining characters; retain CJK name behavior. Version changed input preparation while keeping saved translations reusable.
- Correct installation and unified-upgrade guidance; use the package manifest as the version authority.
- Retain the plugin identity, schema 1, data directory, settings, credentials and existing history. Validation uses only synthetic provider responses.

## 1.0.0-rc.1

- Require the exact supported database definition for offline backup and restore, matching runtime startup. Adoption shares the same compatibility check.
- Reject Windows console aliases, superscript device names and invalid filename characters before archive extraction.
- Translate titles, synopses and dictionary labels from the actual source language, including mixed-language text; version changed prompts without regenerating saved translations.
- Keep the public plugin identity and unified schema/data directory. Existing unified preview installations retain settings, encrypted credentials, local rules and history.
- Candidate release for Jellyfin 12.0.0 / Windows x64; validation uses synthetic provider responses, not real translation requests.

## 0.12.0-preview.2 — Development baseline (not released)

- Use one portable package for general defaults and optional installation-local title/genre rule packs.
- Add explicit offline adoption of an idle compatible schema-1 store, preserving original data and credentials with verified backup/restore and server-ownership checks.
- Show local rule status and fail closed on invalid rules; preserve manual selections and fingerprint custom prompt content.
- Centralize product versions in `Directory.Build.props` and keep private data/rules outside source and release assets.

## 0.12.0-preview.1

- New unified database, one durable queue and one public HTTPS compatible connection.
- Overview/Movies/Settings workflow, draft model lookup, optional test, editable history and automatic display of submitted results.
- Missing-only fill, source/conflict/unknown-outcome protections, searchable paged dictionary/person rules and actionable issues.
- Read pooling, batched detail reads, owner search index, bounded NFO reuse and idle request backoff.
- Separate `metadata-localizer-v2` data; automatic processing starts off. No old configuration/data/job import, multi-service profiles or LM Studio native support.
- New offline unified backup/restore helper; old releases and history are retained.

## 0.11.0-preview.1 — 2026-09-22

- Prepare a standalone general-purpose source tree and manual-install package for Jellyfin 12.0.0 on Windows x64.
- Replace domain-specific translation rules, examples, and genre vocabulary with general movie defaults.
- Accept valid short and numeric movie titles and preserve source names during translation validation.
- Require explicit model configuration before translation; keep provider model discovery available before selecting a model.
- Clarify the bilingual administration interface and overview workflow.
- Support administrator-configured OpenAI-compatible HTTPS cloud services with independent credentials, generic Chat Completions requests, and optional JSON response mode.
- Record the confirmed display separately from title source text, allowing localized movie names while retaining external-edit and recovery protections.
- Distinguish key-file presence, model selection, and connection-test results during setup.
- Build against pinned official Jellyfin NuGet packages without referencing a local server installation.
- Include documented installation, credential setup, backup, privacy boundaries, build checks, and package hashes.

This candidate preserves the existing separation of original sources, translation candidates, and shared-field applications. It retains NFO ownership checks, metadata-saver checks, conflict detection, and restoration preconditions.

This is a preview release for early feedback.
