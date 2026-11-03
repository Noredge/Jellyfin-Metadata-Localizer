# Backup and recovery / 备份与恢复

Stop Jellyfin and retain the complete plugin folder, `data/metadata-localizer-v2`, Jellyfin's host database and configuration as one backup. Keep SQLite sidecar files, encrypted credentials, local rules and `late-responses` with the data directory.

The package includes a Python 3.12+ backup tool. Use absolute paths matching your installation:

```powershell
python tools/Localizer-DataBackup.py backup --source C:\JellyfinData\data\metadata-localizer-v2 --archive C:\Backups\localizer.zip --server-stopped
python tools/Localizer-DataBackup.py restore --archive C:\Backups\localizer.zip --destination C:\RestoreCheck\metadata-localizer-v2 --server-stopped
```

The tool verifies file digests, SQLite integrity, foreign keys and schema compatibility. Restore to a new directory and check it before replacing installed data. Keep the current installation separately so changes after the backup remain recoverable. Encrypted keys require the same Windows account and endpoint.

## Upgrading to 1.0

Compatible unified versions keep the same plugin identity, schema 1 and `metadata-localizer-v2` directory. Replace all five plugin DLLs together and retain existing data; translations and credentials continue to be used.

Older layouts in `data/metadata-localizer` remain separate. Optional offline adoption of a compatible unified store is described in [local rules](LOCAL-RULES.md).

中文：停服后备份插件、完整数据目录及 Jellyfin 主库和配置；先恢复到新目录验证。升级保留现有 `metadata-localizer-v2`，回退前另存后来产生的数据。
