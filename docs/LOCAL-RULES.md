# Local rules and data adoption

One source tree and the same five-DLL package serve both default installations and installations with personal rules. No separate private build is required. Personal rules, credentials and media stay outside the repository and release assets.

## Optional local rules

Stop Jellyfin. Put `local-rules.json` inside `data/metadata-localizer-v2`, then restart. The settings page reports whether local rules are loaded. `tools/local-rules.example.json` is an optional starting point; it is not loaded automatically.

```json
{
  "format": 1,
  "titleRules": {
    "en": {
      "version": "my-rules-v1",
      "instructions": "Use natural English and preserve names, numbers and source meaning.",
      "rules": [
        { "id": "season", "pattern": "season", "instruction": "Keep season numbering." }
      ]
    }
  }
}
```

Supported title languages are `zh-Hans` and `en`. An omitted language uses its embedded general rules. Placeholders and response validation still apply. A content fingerprint is included in new request provenance, even if the human version stays the same. Existing translations and selections are not regenerated or rewritten. Rule files are read once per process; stop Jellyfin before editing and restart to reload.

Optional `genreTerms` replaces the installation's default dictionary with a complete local list:

```json
"genreTerms": [
  { "original": "Drama", "chinese": "剧情", "english": "Drama" }
]
```

Omit `genreTerms` to use the general dictionary. An explicitly empty list supplies no default terms. Existing dictionary/history rows are never deleted, and seeding preserves manual selections. “Restore builtin” uses the configured local defaults when present. Person mappings and manually edited terms remain in the database and continue to use the existing editor.

Limits: UTF-8 JSON (a Windows BOM is accepted), 1 MiB, depth 16, two title languages, 64 terminology rules per language, 5,000 genre terms. Use the camelCase names shown above. Unsupported fields, duplicate properties, malformed regexes and invalid values are rejected. Invalid local rules leave the plugin unavailable with `LocalRulesInvalid`, without opening an executor or falling back silently. Correct the file and restart. Do not put API keys, endpoints or installation settings in this file.

## Explicit offline adoption

Fresh installs continue to use a new `metadata-localizer-v2` directory. No older directory is selected automatically. The optional adoption tool accepts an existing compatible unified schema-1 store, including the former private `.22` layout. It does not import older multi-database formats or merge two stores.

1. Back up the complete plugin, data, credentials and Jellyfin host state. Confirm a rollback restore works.
2. Resolve unfinished tasks, uncertain display writes and unknown request outcomes in the existing plugin. Stop Jellyfin.
3. Prepare personal rules outside the source tree when needed. Get the current server ID from Jellyfin's public system information before stopping.
4. Run the tool without `--apply` for inspection; then add `--apply` to perform the verified copy:

```powershell
python tools/Localizer-AdoptData.py --source C:\JellyfinData\data\metadata-localizer --destination C:\JellyfinData\data\metadata-localizer-v2 --archive C:\Backups\before-adoption.zip --server-id YOUR_SERVER_ID --rules C:\MyRules\local-rules.json --server-stopped
```

The destination must be a new sibling directory named `metadata-localizer-v2`. Existing destinations and backups are never overwritten. The tool checks integrity, foreign keys, the exact schema definition, server ownership and idle work/request state. `--apply` backs up the source, restores into a temporary directory, verifies all original file bytes, optionally adds the local rule file, then commits the new directory. The source remains intact. Settings, translation versions, manual edits, selections, task states, display history, person rules and encrypted credential files are copied together. No schema migration, task reset/replay or provider call occurs. The plugin performs full rule-content validation at startup.

Install the unified package with the public plugin ID, keeping the old plugin folder outside Jellyfin's plugin scan directory. Never load both packages together. Credentials remain bound to the same Windows account and endpoint. Verify Ready/Active, rule status, original counts, display/NFO preservation and no new requests before resuming normal use. Retain the paired rollback backup. The plugin-only ZIP does not roll back changes in Jellyfin's own database; see [backup](BACKUP.md).
