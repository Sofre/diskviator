# DiskViator

A forensic disk space historian for Windows. It watches a drive for writes, records what grew, maps folder sizes, and helps you find what is filling up your disk. It never deletes files on its own.

For install steps and the full command reference, see [help.md](help.md).

## Install

From a PowerShell window in the project folder, run:

```powershell
.\install.ps1
```

Or double-click `install.bat`. The installer publishes a self-contained Windows app to `%LOCALAPPDATA%\DiskViator\app` and creates a `diskviator` command under `%LOCALAPPDATA%\DiskViator\bin`. It adds that command directory to your user PATH; open a new terminal afterward. Run the installer again to update. To uninstall the app and command while keeping the database, run `.\install.ps1 -Uninstall`.

## Automatic background tracking

Installing DiskViator does **not** automatically start disk tracking. To enable it, open an **Administrator PowerShell** and run:

```powershell
diskviator --install-startup
```

This registers a Windows Task Scheduler task named **DiskViator Background Tracking** to run DiskViator in tray mode at each user logon, with highest privileges. Tray mode watches the folders configured through menu option **11**; if no folders have been configured, it watches `C:\`. Option 11 saves the folders, updates a running tray monitor (or starts one), and registers automatic startup. Tray mode detaches from the launching terminal and shows a traveler icon in the notification area. You can close the terminal after running `diskviator --tray` without stopping tracking. Right-click the icon and choose **Exit DiskViator tracking** to stop tracking for the current session. Alerts identify folders with high write churn or burst activity, with a 15-minute cooldown per folder.

The task runs again at the next logon; choosing Exit does not disable automatic startup. To disable it, run this from an elevated terminal:

```powershell
diskviator --remove-startup
```

The tray prunes database rows older than 30 days at startup and every six hours while it is running. It does not automatically scan the whole drive or delete files. A separate interactive session can run its own watcher, so avoid running both watchers at once if you do not want duplicate tracking.

## Features

### 1. Background Tracking (live monitor) - STAYS -- PROCESS

Watches a drive or folder with a `FileSystemWatcher` and records writes to the database.

- Menu option **1**. Prompts for a path (default `C:\`).
- Only files **5 MB or larger** are saved to history. Smaller files are ignored by the history log.
- Repeated events for the same file are debounced for 3 seconds, so one save is logged once.
- Each saved file is tagged **SYSTEM** (Windows, Program Files\WindowsApps, ProgramData\Microsoft, `$Recycle.Bin`, system DLL/driver extensions) or **SAFE**.
- Press `Enter` to return to the menu. Tracking keeps running in the background of the session.
- Watcher errors and permission-denied or locked files are counted and suppressed, so one bad file does not stop monitoring.

### 2. Hot Write Paths Radar

Shown on the live tracking screen (option 1). It answers "which folder is being written to right now?"

- Looks at **every** write, including small ones (no 5 MB floor), over a trailing **5-minute** window.
- Groups writes by parent folder and reports two independent signals:
  - **Churn**: many writes per minute (alert at **20 writes/min**). Typical of logs, caches, and temp files. 
  - **Burst**: many megabytes per minute (alert at **50 MB/min**). Typical of large copies, downloads, or VM disks.
- Rates use the time since tracking started, capped at 5 minutes, so the first minute is not underreported.
- Nothing is deleted. The radar is for you to decide what to clean up.

### 3. 15-Day Rolling History

Menu option **2**.

- Sums the MB written per calendar day for the last 15 days from `FileHistory` and draws a bar chart.
- Only files that passed the 5 MB history filter are counted.

### 4. Top 10 Largest Files (Min Size)

Menu option **3**.

- Prompts for a minimum size in MB (default **500**). That is the only filter: no dates, no time windows.
- Queries `FileHistory` for files at or above that size.
- **Deduplicates by path**: a file written many times (a log, a VM disk, the pagefile) appears once, using the **largest size seen**.
- Sorts by size and shows the top 10, with each row's **ID**. Use that ID with option 6 or 8.

Important: this lists the largest files that were **written** while tracking was running, as recorded by the watcher, and only those 5 MB or larger. It is not a scan of the whole disk. For a full-disk view, use Map Out Drive (option 5).

### 5. Monthly Folder Accumulation

Menu option **4**.

- Takes every `FileHistory` row from the current calendar month and groups them by folder.
- Shows the top 5 folders by total MB, with counts of SYSTEM and SAFE files in each.

### 6. Map Out Drive (Folder Inventory)

Menu option **5**.

- Prompts for a root folder (default `C:\`) and a minimum size in MB (default `10`).
- Walks the tree recursively. Each folder's size is the sum of its own files plus all subfolders.
- Every folder at or above the threshold is saved to `FolderMap` under a new scan ID (timestamp `yyyyMMdd-HHmmss`).
- Each saved folder records its total size, file count, subfolder count, a SYSTEM flag, and a best-guess description (for example "Windows Update staging/download cache" or "Node.js dependencies"). Descriptions come from a pattern list in `FolderDescriptionLookup`. The first matching pattern wins.
- Folders you cannot read (access denied) are skipped and counted.
- Unlike tracking, this sees data that **already exists** on disk, not only new writes. A full scan can take a long time.

### 7. Clean Database 1 (File History Log)

Menu option **6**. Opens a submenu:

- **Delete specific rows by ID**: enter one or more IDs separated by commas (for example `4, 17, 20`), confirm, and only those rows are removed. IDs are shown in the Top 10 and raw table views.
- **Wipe the entire table**: asks for confirmation (default **No**), then deletes **all** rows in `FileHistory` and runs `VACUUM` to reclaim disk space. It cannot be undone.
- **Back**: return to the menu.

### 8. Clean Database 2 (Folder Map Data)

Menu option **7**. Same submenu as option 6, for `FolderMap` (all Map Out scans).

### 9. View & Filter Database Tables

Menu option **8**.

- Choose `FileHistory` or `FolderMap`, then set optional filters. Leave any filter blank to skip it. All filters you fill in must match.
  - ID (exact match)
  - `FileHistory`: filename contains, extension, minimum size (MB), from date, to date (inclusive, through 23:59:59)
  - `FolderMap`: scan ID, folder path contains, minimum folder size (MB)
- Then enter how many rows to show (default 50). Rows come back newest first, with the ID column first.

### 10. Folder Growth Since Last Scan

Menu option **9**.

- Compares the two most recent Map Out scans folder by folder.
- Each folder is labelled **NEW**, **REMOVED**, **GROWN**, or **SHRUNK**. Folders with no change are left out.
- Sorted by the largest absolute change, top 20.
- Needs at least two Map Out scans (option 5).

### 11. Tray Monitoring Setup

Menu option **11**.

- Select one or more existing folders for tray monitoring. Duplicate or nested folders are rejected to avoid recording the same writes more than once.
- Saves the selection for tray mode, updates an already-running tray monitor or starts one immediately, and registers the Windows logon task.
- Run DiskViator from an **Administrator terminal** so the scheduled task can run with highest privileges. If task registration fails, tray monitoring still starts now, and the menu reports that automatic startup was not enabled.
- The tray configuration is stored at `%LOCALAPPDATA%\DiskViator\TrayMonitoring.json`.

## Background tray and startup

- `diskviator --tray` runs tracking with no console window. It shows a tray icon (right-click it → **Exit DiskViator tracking** to stop) and balloon notifications when a folder shows churn or burst write activity. Alerts are per folder, with a 15-minute cooldown.
- `diskviator --install-startup` registers a Task Scheduler job, **DiskViator Background Tracking**, that runs `--tray` at logon with highest privileges. **Run this from an elevated (Administrator) terminal.**
- `diskviator --remove-startup` removes that job.
- Tray mode watches `C:\` by default. The interactive menu and the tray are separate processes. Both write to the same database, and each runs its own watcher, so avoid running heavy tracking in both at once.

## Data and storage

- Database: `%LOCALAPPDATA%\DiskViator\DiskHistorian.db` (SQLite, WAL mode). The location is the same no matter which folder you launch from.
- Tables:
  - `FileHistory`: one row per recorded write (path, name, extension, size, system flag, timestamp).
  - `FolderMap`: one row per folder per scan (scan ID, path, size, counts, system flag, description, timestamp).
- **Automatic retention**: rows older than **30 days** are deleted at startup and every 6 hours while the tray runs. This applies to both tables, including old Map Out scans. Option 9 (growth since last scan) needs two scans inside that 30-day window.
- The in-memory write radar and the activity log are not saved. They reset when the program exits.

## Running

```powershell
cd "d:\Bekap star disk\diskastorian"
dotnet run
```

Run the terminal as Administrator to monitor protected folders such as `C:\Windows`.
