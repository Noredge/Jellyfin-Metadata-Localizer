# Changelog

## 0.11.0-preview.1 — local review candidate

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

No public release or GitHub Actions run has occurred for this candidate.
