# DiskViator — CLI COMMANDS

## One-line install (from anywhere, no download needed)

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/Sofre/diskviator/main/install.ps1 | iex
```

This downloads the source straight from GitHub, installs the .NET SDK if it's missing, publishes the self-contained Windows app, and adds the `diskviator` command to your user PATH. Re-running it safely updates the existing install.

Then open a NEW terminal window and run it from any folder:

```powershell
diskviator
```

## Local install (if you already have the source on disk)

```powershell
cd "d:\Bekap star disk\diskastorian"
.\install.ps1
```

Or just double-click `install.bat`.

Uninstall:

```powershell
$installer = Join-Path $env:TEMP 'diskviator-install.ps1'
Invoke-WebRequest 'https://raw.githubusercontent.com/Sofre/diskviator/main/install.ps1' -OutFile $installer
& $installer -Uninstall
Remove-Item $installer
```

> Run your terminal as Administrator to monitor protected folders like `C:\Windows`.
> The database always lives at `%LOCALAPPDATA%\DiskViator\DiskHistorian.db`, regardless of which folder you run `diskviator` from.

Uninstalling preserves the database. Open a new terminal after installing or uninstalling so the updated PATH takes effect.

## Manual publish (without adding the `diskviator` command)

```powershell
cd "d:\Bekap star disk\diskastorian"
dotnet publish -c Release -r win-x64 --self-contained true -o "$env:LOCALAPPDATA\DiskViator\app"
& "$env:LOCALAPPDATA\DiskViator\app\DiskViator.exe"
```

## Dev mode (run from source without installing)

```powershell
cd "d:\Bekap star disk\diskastorian"
dotnet restore
dotnet build
dotnet run
```

## Menu Commands

| # | Command | Action |
|---|---------|--------|
| 1 | Start Background Tracking | Prompts for drive/folder (default `C:\`), starts FileSystemWatcher. Live view shows a **Hot Write Paths radar** (churn = writes/min, burst = MB/min, per folder, 5-min window) plus the activity log. Press `Enter` to return to menu (keeps running). |
| 2 | View 15-Day Rolling History | Shows BarChart of MB written per day, last 15 days. |
| 3 | Top 10 Largest Files (Min Size) | Prompts for a minimum size in MB (default 500). Shows the top 10 largest **distinct** recorded files at or above that size, with their IDs. No date filter. |
| 4 | Monthly Folder Accumulation | Shows top 5 folders by total MB this month. |
| 5 | Map Out Drive (Folder Inventory) | Prompts: root path (default `C:\`), MB threshold (default 10). Recursively walks every folder from root, computing total size/file count/subfolder count. Folders at/above the threshold are saved to `FolderMap` (tagged system/not-system + a best-guess description). Shows top folders by size when done. Skips access-denied folders. |
| 6 | Clean Database 1 (File History Log) | Submenu: **delete specific rows by ID** (comma-separated, confirmed), **wipe the entire table** (y/N, default No, `VACUUM` after), or back. |
| 7 | Clean Database 2 (Folder Map Data) | Same submenu as option 6, for `FolderMap` (all Map Out scans). |
| 8 | View & Filter Database Tables | Pick `FileHistory` or `FolderMap`, set optional filters (ID, name/extension/size/date range for FileHistory; scan ID/path/size for FolderMap), and a row limit (default 50). Blank filters are skipped. Newest rows first, with ID column. |
| 9 | Folder Growth Since Last Scan | Compares the two most recent Map Out scans folder-by-folder. Shows which folders grew, shrunk, newly appeared, or disappeared since the previous scan, sorted by biggest change first. Needs at least 2 Map Out scans (option 5) to work. |
| 10 | Exit | Stops watcher and quits. |
| 11 | Setup Tray Monitoring (Folders) | Choose one or more non-overlapping folders. Saves the tray configuration, starts or updates tray monitoring now, and registers automatic tray startup at Windows logon. Run as Administrator to register the scheduled task with highest privileges. |

Navigation: `↑`/`↓` to select, `Enter` to confirm.

## Background tray and startup flags

| Command | Action |
|---------|--------|
| `diskviator --tray` | Runs background tracking of `C:\` in the notification area with balloon alerts. Exit from the tray icon's menu. Only one tray instance runs per user. |
| `diskviator --install-startup` | Registers the Task Scheduler job **DiskViator Background Tracking** (at logon, highest privileges). Run from an **elevated** terminal. |
| `diskviator --remove-startup` | Removes that job. |

Tray monitoring uses the folders saved by menu option 11, or `C:\` if none have been configured. Rows older than 30 days are deleted automatically at startup and every 6 hours in tray mode.
