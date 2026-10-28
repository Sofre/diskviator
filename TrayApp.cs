using System.Drawing;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace DiskViator;

/// <summary>
/// Entry point for the background tray mode (<c>--tray</c>). Only one tray instance runs per user session.
/// </summary>
internal static class TrayHost
{
    private const string MutexName = @"Local\DiskViator.Tray";
    private const string ReloadEventName = @"Local\DiskViator.Tray.Reload";

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    public static bool NotifyConfigurationChanged()
    {
        try
        {
            using var reloadEvent = EventWaitHandle.OpenExisting(ReloadEventName);
            reloadEvent.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public static void StartDetached()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the executable path.");
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        if (Path.GetFileNameWithoutExtension(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(typeof(TrayHost).Assembly.Location);
        }
        startInfo.ArgumentList.Add("--tray");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start tray monitoring.");
    }

    public static void Run()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return; // tray tracking is already running
        }

        try
        {
            using var reloadEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ReloadEventName);
            if (GetConsoleWindow() != IntPtr.Zero && !FreeConsole())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not detach background tracking from the terminal.");
            }

            DiskHistoryDatabase.Initialize();
            DiskHistoryDatabase.PruneOlderThan(DiskHistoryDatabase.RetentionDays);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using var context = new TrayApplicationContext(TrayMonitoringSettings.Load(), reloadEvent);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"DiskViator background tracking stopped: {ex.Message}", "DiskViator", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }
}

/// <summary>
/// Runs the file watcher in the background, shows a tray icon with an Exit menu,
/// and balloon-notifies when a folder shows churn or burst write activity.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    private readonly EventWaitHandle _reloadEvent;
    private List<DiskWatcherService> _watchers;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly System.Windows.Forms.Timer _alertTimer;
    private readonly System.Windows.Forms.Timer _pruneTimer;
    private readonly System.Windows.Forms.Timer _reloadTimer;
    private readonly Dictionary<string, DateTime> _lastAlertUtc = new(StringComparer.OrdinalIgnoreCase);

    public TrayApplicationContext(IReadOnlyList<string> rootPaths, EventWaitHandle reloadEvent)
    {
        _reloadEvent = reloadEvent;
        _trayIcon = LoadTrayIcon();
        _watchers = CreateWatchers(rootPaths);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit DiskViator tracking", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = $"DiskViator - tracking {rootPaths.Count} folder(s)",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _alertTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _alertTimer.Tick += (_, _) => CheckHotFolders();
        _alertTimer.Start();

        _pruneTimer = new System.Windows.Forms.Timer { Interval = (int)PruneInterval.TotalMilliseconds };
        _pruneTimer.Tick += (_, _) => PruneOldRows();
        _pruneTimer.Start();

        _reloadTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _reloadTimer.Tick += (_, _) =>
        {
            if (_reloadEvent.WaitOne(0))
            {
                ReloadWatchers();
            }
        };
        _reloadTimer.Start();

        _notifyIcon.ShowBalloonTip(3000, "DiskViator", $"Background tracking started on {FormatPaths(rootPaths)}", ToolTipIcon.Info);
    }

    private static List<DiskWatcherService> CreateWatchers(IEnumerable<string> rootPaths)
    {
        var watchers = new List<DiskWatcherService>();
        try
        {
            foreach (string rootPath in rootPaths)
            {
                var watcher = new DiskWatcherService(rootPath);
                watchers.Add(watcher);
                watcher.Start();
            }
            return watchers;
        }
        catch
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
            throw;
        }
    }

    private void ReloadWatchers()
    {
        try
        {
            List<DiskWatcherService> newWatchers = CreateWatchers(TrayMonitoringSettings.Load());
            List<DiskWatcherService> oldWatchers = _watchers;
            _watchers = newWatchers;
            foreach (var watcher in oldWatchers)
            {
                watcher.Dispose();
            }
            _lastAlertUtc.Clear();
            _notifyIcon.ShowBalloonTip(3000, "DiskViator", $"Tray folders updated: {FormatPaths(newWatchers.Select(watcher => watcher.RootPath))}", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(5000, "DiskViator configuration error", ex.Message, ToolTipIcon.Error);
        }
    }

    private static string FormatPaths(IEnumerable<string> paths)
    {
        string[] pathList = paths.ToArray();
        return pathList.Length == 1 ? pathList[0] : $"{pathList.Length} folders";
    }

    private static Icon LoadTrayIcon()
    {
        const string resourceName = "DiskViator.icons8-traveler-64-32x32.ico";
        using Stream stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded tray icon resource '{resourceName}' was not found.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private void CheckHotFolders()
    {
        var nowUtc = DateTime.UtcNow;

        foreach (var folder in _watchers.SelectMany(watcher => watcher.GetHotFolders(5)))
        {
            if (!folder.IsBurstAlert && !folder.IsChurnAlert)
            {
                continue;
            }

            if (_lastAlertUtc.TryGetValue(folder.FolderPath, out var lastAlert) && nowUtc - lastAlert < AlertCooldown)
            {
                continue;
            }

            _lastAlertUtc[folder.FolderPath] = nowUtc;

            string reason = folder.IsBurstAlert
                ? $"{folder.MbPerMinute:F0} MB/min written"
                : $"{folder.EventsPerMinute:F0} writes/min";

            _notifyIcon.ShowBalloonTip(5000, "DiskViator alert", $"{folder.FolderPath}: {reason}", ToolTipIcon.Warning);
            break; // one balloon per poll; the rest are picked up on later polls
        }

        // Forget folders whose cooldown has ended so the dictionary doesn't grow forever.
        foreach (var key in _lastAlertUtc.Where(kv => nowUtc - kv.Value >= AlertCooldown).Select(kv => kv.Key).ToList())
        {
            _lastAlertUtc.Remove(key);
        }
    }

    private static void PruneOldRows()
    {
        try
        {
            DiskHistoryDatabase.PruneOlderThan(DiskHistoryDatabase.RetentionDays);
        }
        catch (SqliteException)
        {
            // Database busy or locked: the next prune interval will try again.
        }
    }

    protected override void ExitThreadCore()
    {
        _alertTimer.Stop();
        _pruneTimer.Stop();
        _reloadTimer.Stop();
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _alertTimer.Dispose();
        _pruneTimer.Dispose();
        _reloadTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }
}
