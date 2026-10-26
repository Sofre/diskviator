# DiskViator — CLI COMMANDS

## One-line install (from anywhere, no download needed)

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/Sofre/diskviator/main/install.ps1 | iex
```

This downloads the source straight from GitHub, installs the .NET SDK if it's missing, builds, and installs `diskviator` globally — no manual clone/download, no visible build noise, just a few short status lines. Re-running it later safely updates the existing install.

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
dotnet tool uninstall -g DiskViator
```

> Run your terminal as Administrator to monitor protected folders like `C:\Windows`.
> The database always lives at `%LOCALAPPDATA%\DiskViator\DiskHistorian.db`, regardless of which folder you run `diskviator` from.

## Manual install (if you prefer doing it step by step)

```powershell
cd "d:\Bekap star disk\diskastorian"
dotnet pack -c Release
dotnet tool install -g DiskViator --add-source ./nupkg
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
| 3 | Top 10 Largest Files (Date Range) | Prompts: Start date (yyyy-MM-dd), Start time (HH:mm, optional, default 00:00), End date (optional, default today), End time (optional, default now). Shows top 10 largest **distinct** files in that window (dedup'd by path, largest size wins). |
| 4 | Monthly Folder Accumulation | Shows top 5 folders by total MB this month. |
| 5 | Map Out Drive (Folder Inventory) | Prompts: root path (default `C:\`), MB threshold (default 10). Recursively walks every folder from root, computing total size/file count/subfolder count. Folders at/above the threshold are saved to `FolderMap` (tagged system/not-system + a best-guess description). Shows top folders by size when done. Skips access-denied folders. |
| 6 | Clean Database 1 (File History Log) | Asks for confirmation (y/N, default No), then **permanently deletes every row** in `FileHistory` (the write/history log used by History/Top Files/Monthly reports) and reclaims disk space. Not reversible. |
| 7 | Clean Database 2 (Folder Map Data) | Asks for confirmation (y/N, default No), then **permanently deletes every row** in `FolderMap` (the Map Out scan data) and reclaims disk space. Not reversible. |
| 8 | Exit | Stops watcher and quits. |

Navigation: `↑`/`↓` to select, `Enter` to confirm.
