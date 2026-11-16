# StarStrings Updater

A portable Windows 10/11 x64 tray app for [MrKraken’s StarStrings LIVE releases](https://github.com/MrKraken/StarStrings/releases). The release is a single, self-contained executable; no installer or separate .NET installation is needed.

## Use

1. Download `StarStringsUpdater.exe` from the [latest release](https://github.com/Manaconda/StarStringsUpdater/releases/latest), put it somewhere permanent, and open it.
2. Check the detected **LIVE** installation folder. Edit it, use **Browse**, or press **Detect** if needed. Detection checks RSI/Star Citizen uninstall registry locations and common game directories on fixed drives. Other layouts can be selected manually.
   On first opening, the app checks write access to the detected folder. **Save settings** and **Check / update** also check the selected path. If Windows denies access, it offers a one-time administrator prompt to set up silent updates. Declining or cancelling leaves setup incomplete; Save settings lets you retry.
3. Press **Check / update StarStrings** to save configuration and install the latest LIVE release if needed.
4. Optionally select **Run at Windows startup**, then **Save settings**. This registers the current executable location for your Windows account. If you later move the executable, open it at its new location and save settings again.

Closing or minimizing the window leaves the app in the tray. Hover over its star icon to see installed/available versions, left-click to open configuration, or right-click and choose **Exit**. Launching the executable again opens the existing instance.

A normal launch opens configuration and checks availability without installing. A Windows sign-in launch (`--startup`) checks and installs silently, with no window or notification balloons. Once settings have been saved, the running tray app also checks and installs hourly. Exit stops these checks until the app starts again. Background errors are recorded in the log and displayed when you open configuration. Network failures and a running game are retried at the next hourly check.

The first installation managed by this app establishes version tracking. Previously installed files are shown as **Untracked or changed files** until that update succeeds. The app also detects changed/missing strings and repairs them on an update. Available versions are the last successful GitHub check, not a continuous live feed.

## Installation behaviour

- Uses the release’s `StarStrings-LIVE.zip` asset, never PTU or GitHub’s source-code ZIP.
- Tracks release ID, asset ID, update timestamp and checksum because upstream reuses the `latest` tag.
- Checks downloaded size and GitHub’s SHA-256 digest when provided.
- Installs `Data/Localization/english/global.ini`; merges `g_language = english` into `USER.cfg` while preserving other settings and common text encodings, following the [upstream installation instructions](https://github.com/MrKraken/StarStrings/blob/master/readme.md).
- Rejects unexpected payload files, path traversal, duplicate paths and linked destination folders/files. Limits downloads and extracted contents to 100 MB.
- Backs up replaced files, writes files through temporary files, and restores earlier files on an ordinary installation failure. Forced process termination or power loss during the multi-file commit is not transactional; retained backups allow manual recovery.
- Defers updates when Star Citizen is running. Do not start the game during installation.

The LIVE folder must be writable by your Windows account for silent updates. Interactive setup tests actual file operations and access to existing payload files. When access is denied, you can approve a one-time UAC prompt. A short-lived elevated copy of the same executable grants **your original Windows account** Modify access to LIVE itself, the Data/Localization/english directory chain, and existing USER.cfg, global.ini and version receipt files. Directory grants do not inherit into unrelated game files. Existing ACL entries are preserved, and earlier ACLs are saved under the elevated account’s `%LOCALAPPDATA%\StarStringsUpdater\PermissionBackups`.

The original unelevated app verifies access again before saving settings. Existing explicit Deny rules or device restrictions may still prevent access; setup reports failure rather than assuming elevation fixed it. Locked files and read-only attributes are reported separately and do not trigger UAC. Cancelling UAC is handled without installing or enabling startup. New updater-created files retain Modify access for the creating account. Background startup never requests elevation; if permissions later change, open configuration and save settings to rerun setup.

## Local files and removal

- Settings: `%LOCALAPPDATA%\StarStringsUpdater\settings.json`
- Log: `%LOCALAPPDATA%\StarStringsUpdater\updater.log` (rotated at 2 MB)
- Backups: `%LOCALAPPDATA%\StarStringsUpdater\Backups\<timestamp-id>`; `backup.json` records the destination and which files existed before the update. Backups are retained until you remove them.
- Version receipt: `<LIVE>\.starstrings-updater.json`
- Startup registration: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\StarStringsUpdater`

To remove the updater, uncheck startup, save, exit using the tray menu, and delete the executable. Settings/backups can be deleted separately. This does not uninstall the StarStrings game files.

The executable is unsigned. It is an independent updater, not an official RSI or StarStrings application.

## Build and test

Requires the .NET 10 SDK on Windows:

```powershell
.\build.ps1
dotnet run --project tests\Tests.csproj -c Release -- "$PWD"
```

The tests expect a copy of the current official release ZIP at `.tools/fixtures/StarStrings-LIVE.zip`. They use a temporary mock LIVE installation under `test-results`, and also exercise the live GitHub API, real download, and checksum rejection. They never install to your real game directory. Network access is required. Test installations produce local backups in the same location as normal updates.

The build output is `dist/StarStringsUpdater.exe` (approximately 49 MB). Source is in `StarStringsUpdater/`. No third-party runtime packages are used.
