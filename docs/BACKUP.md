# Backup and recovery / 备份与恢复

Stop Jellyfin before filesystem backup or restore. Preserve the plugin version together with compatible data, configuration and the Jellyfin host database. A plugin-only backup does not capture Jellyfin's displayed metadata or undo host changes.

## Unified plugin data

Back up the entire `data/metadata-localizer-v2` directory, including `metadata-localizer.db`, any `-wal`, `-shm` or `-journal` files, encrypted credentials and worker-lock file. Do not select only the main DB while a WAL exists. Keys remain bound to the Windows account and endpoint.

The ZIP includes `tools/Localizer-DataBackup.py` (Python 3.12+, standard library). Supply absolute paths, using the actual server data location:

```powershell
python tools/Localizer-DataBackup.py backup --source C:\JellyfinData\data\metadata-localizer-v2 --archive C:\Backups\localizer.zip --server-stopped
python tools/Localizer-DataBackup.py restore --archive C:\Backups\localizer.zip --destination C:\RestoreCheck\metadata-localizer-v2 --server-stopped
```

The tool requires stopped-server acknowledgement, rejects an active worker lock, preserves all files, checks SQLite integrity and foreign keys on an independent copy, and verifies SHA-256. Restore creates a new directory, never overwrites an installation, and rejects unsafe archive paths. Limits: 10,000 files and 1 GiB of plugin data. Test a restore before relying on a backup. Treat archives as private even though keys are encrypted.

To replace an installed directory, keep Jellyfin stopped, retain the current directory separately, and move the independently verified restored directory into place. Do not discard data created after the backup. Start and inspect logs, library display and request state. Never edit request evidence to bypass unknown-outcome confirmation.

## Returning to 0.11

Keep old `data/metadata-localizer` and the old plugin separately. Version 0.12 neither migrates nor rewrites them. Switching DLLs does not undo metadata displayed by 0.12: use guarded restore actions or restore the complete paired Jellyfin backup from before the switch. Do not mix versions' DLLs or point an old version at the new DB. Old Release and Git history remain available.

中文提示：停服后整目录备份并保全SQLite旁文件和加密密钥。工具只备份插件数据，完整回退还需要配套的Jellyfin主库及配置。恢复到全新目录校验；覆盖安装前另存当前数据。
