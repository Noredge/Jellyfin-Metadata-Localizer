# Local rules

Optional installation rules live in `data/metadata-localizer-v2/local-rules.json`. Stop Jellyfin before editing and restart to load them. The settings page reports whether they loaded. Start with the packaged `tools/local-rules.example.json`.

```json
{
  "format": 1,
  "titleRules": {
    "en": {
      "version": "my-rules-v1",
      "instructions": "Use natural English and preserve names, numbers and source meaning.",
      "rules": []
    }
  },
  "genreTerms": [
    { "original": "Drama", "chinese": "剧情", "english": "Drama" }
  ]
}
```

Title languages are `zh-Hans` and `en`. Omitted languages use the general rules. Omit `genreTerms` to use the general dictionary; when supplied it defines the installation's complete default list. Existing translations and manual dictionary selections are retained. Person-name mappings are edited in the plugin page.

Use UTF-8 JSON and the field names above. Limits are 1 MiB, 64 terminology rules per language and 5,000 genre terms. Connection settings and API keys belong in the settings page.

## Adopt a compatible unified store

For an existing schema-1 store in an older data directory, the offline tool can copy its settings, history, rules and credentials into a new sibling `metadata-localizer-v2` directory. First make a paired backup, finish pending work, and stop Jellyfin. Get the server ID from Jellyfin's system information.

```powershell
python tools/Localizer-AdoptData.py --source C:\JellyfinData\data\metadata-localizer --destination C:\JellyfinData\data\metadata-localizer-v2 --archive C:\Backups\before-adoption.zip --server-id YOUR_SERVER_ID --server-stopped
```

The command first inspects compatibility. Add `--apply` to back up and copy the store; add `--rules <path>` for an optional local rules file. The source remains available for rollback. Use the same Windows account for encrypted credentials. See [backup and recovery](BACKUP.md).
