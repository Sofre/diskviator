// =============================================================================
// DiskViator — Forensic Disk Space Historian
// .NET 8 Console App | Spectre.Console UI | Microsoft.Data.Sqlite storage
// =============================================================================

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DiskViator;

// =============================================================================
// MODELS
// =============================================================================

/// <summary>A single row pulled from the FileHistory table.</summary>
internal sealed record FileRecord(
    long Id,
    string FilePath,
    string FileName,
    string Extension,
    long SizeBytes,
    bool IsSystemFile,
    DateTime Timestamp)
{
    public double SizeMb => SizeBytes / 1024.0 / 1024.0;
}

/// <summary>Aggregated size (in MB) written on a given calendar day.</summary>
internal sealed record DailyUsage(DateTime Day, double TotalMb);

/// <summary>Aggregated accumulation for a single parent folder in the current month.</summary>
internal sealed record FolderAccumulation(
    string FolderPath,
    double TotalMb,
    int SystemFileCount,
    int SafeFileCount);

/// <summary>
/// Rolling write-activity stats for a single folder over the trailing monitoring window.
/// Used to spot "churn" (many small writes, e.g. app/browser cache) vs. "burst"
/// (a lot of bytes written fast, e.g. a big file being created/copied).
/// </summary>
internal sealed record FolderActivity(
    string FolderPath,
    int WriteCount,
    double EventsPerMinute,
    double MbPerMinute,
    bool IsChurnAlert,
    bool IsBurstAlert);

/// <summary>
/// One folder's entry from a "Map Out" drive scan — a point-in-time inventory row,
/// not a live-activity row. Folders below the scan's size threshold are never stored.
/// </summary>
internal sealed record FolderMapEntry(
    string ScanId,
    string FolderPath,
    string ParentPath,
    long TotalSizeBytes,
    int FileCount,
    int SubfolderCount,
    bool IsSystemFolder,
    string Description,
    DateTime ScannedAt,
    long Id = 0)
{
    public double TotalSizeMb => TotalSizeBytes / 1024.0 / 1024.0;
}

/// <summary>
/// One folder's size comparison between two Map Out scans — used to answer
/// "has this folder grown since last time I scanned?"
/// </summary>
internal sealed record FolderGrowthRow(
    string FolderPath,
    long OldSizeBytes,
    long NewSizeBytes,
    string Status) // "NEW", "REMOVED", "GROWN", or "SHRUNK"
{
    public double OldSizeMb => OldSizeBytes / 1024.0 / 1024.0;
    public double NewSizeMb => NewSizeBytes / 1024.0 / 1024.0;
    public double DeltaMb => NewSizeMb - OldSizeMb;
}

// =============================================================================
// DATABASE LAYER (thread-safe)
// =============================================================================

/// <summary>Optional filters for the raw FileHistory viewer. Null fields are ignored.</summary>
internal sealed record FileHistoryFilter(
    long? Id = null,
    string? NameContains = null,
    string? Extension = null,
    double? MinSizeMb = null,
    DateTime? From = null,
    DateTime? To = null,
    bool? IsSystemFile = null);

/// <summary>Optional filters for the raw FolderMap viewer. Null fields are ignored.</summary>
internal sealed record FolderMapFilter(
    long? Id = null,
    string? ScanId = null,
    string? PathContains = null,
    double? MinSizeMb = null);

/// <summary>
/// Owns all SQLite access. Writes are serialized behind a single lock and each
/// operation uses its own short-lived connection (with WAL + busy_timeout) so
/// that concurrent FileSystemWatcher callbacks never collide on a shared
/// transaction/connection, which is what causes "cannot rollback - no
/// transaction is active" and "database is locked" errors.
/// </summary>
internal static class DiskHistoryDatabase
{
    private const string DbFileName = "DiskHistorian.db";

    /// <summary>Days of history kept in FileHistory and FolderMap. Older rows are deleted automatically.</summary>
    public const int RetentionDays = 30;

    // Installed as a global CLI tool, DiskViator can be launched from any working directory,
    // so the DB must live in a fixed, per-user location rather than "wherever you happen to run it".
    private static readonly string DbFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskViator",
        DbFileName);

    private static readonly string ConnectionString = $"Data Source={DbFilePath};Cache=Shared";
    private static readonly object WriteLock = new();

    /// <summary>Full path to the SQLite database file, for display purposes.</summary>
    public static string DatabasePath => DbFilePath;

    public static void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbFilePath)!);

        lock (WriteLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS FileHistory (
                    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath     TEXT     NOT NULL,
                    FileName     TEXT     NOT NULL,
                    Extension    TEXT     NOT NULL DEFAULT '',
                    SizeBytes    INTEGER  NOT NULL,
                    IsSystemFile INTEGER  NOT NULL DEFAULT 0,
                    Timestamp    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS IX_FileHistory_Timestamp ON FileHistory (Timestamp);
                CREATE INDEX IF NOT EXISTS IX_FileHistory_FilePath ON FileHistory (FilePath);

                CREATE TABLE IF NOT EXISTS FolderMap (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    ScanId          TEXT     NOT NULL,
                    FolderPath      TEXT     NOT NULL,
                    ParentPath      TEXT     NOT NULL DEFAULT '',
                    TotalSizeBytes  INTEGER  NOT NULL,
                    FileCount       INTEGER  NOT NULL DEFAULT 0,
                    SubfolderCount  INTEGER  NOT NULL DEFAULT 0,
                    IsSystemFolder  INTEGER  NOT NULL DEFAULT 0,
                    Description     TEXT     NOT NULL DEFAULT '',
                    ScannedAt       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                CREATE INDEX IF NOT EXISTS IX_FolderMap_ScanId ON FolderMap (ScanId);
                CREATE INDEX IF NOT EXISTS IX_FolderMap_Size ON FolderMap (TotalSizeBytes);
                """;
            command.ExecuteNonQuery();
        }
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        // WAL mode allows readers and the single writer to coexist peacefully,
        // and the busy timeout makes SQLite retry instead of throwing instantly
        // if the file is momentarily touched by the OS/another handle.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>Thread-safe insert used by the FileSystemWatcher callbacks.</summary>
    public static void InsertFileRecord(string filePath, string fileName, string extension, long sizeBytes, bool isSystemFile)
    {
        lock (WriteLock)
        {
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO FileHistory (FilePath, FileName, Extension, SizeBytes, IsSystemFile, Timestamp)
                    VALUES ($path, $name, $ext, $size, $isSys, $ts);
                    """;
                command.Parameters.AddWithValue("$path", filePath);
                command.Parameters.AddWithValue("$name", fileName);
                command.Parameters.AddWithValue("$ext", extension);
                command.Parameters.AddWithValue("$size", sizeBytes);
                command.Parameters.AddWithValue("$isSys", isSystemFile ? 1 : 0);
                command.Parameters.AddWithValue("$ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch (SqliteException)
            {
                // Swallow transient SQLite contention (e.g. locked db from an external
                // viewer). Losing a single historian entry is preferable to crashing
                // the whole background watcher.
            }
        }
    }

    /// <summary>Total MBs written per calendar day for the last <paramref name="days"/> days.</summary>
    public static List<DailyUsage> GetRollingDailyUsage(int days = 15)
    {
        var results = new List<DailyUsage>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT date(Timestamp) AS Day, SUM(SizeBytes) AS TotalBytes
            FROM FileHistory
            WHERE date(Timestamp) >= date('now', $range)
            GROUP BY Day
            ORDER BY Day ASC;
            """;
        command.Parameters.AddWithValue("$range", $"-{days} days");

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var day = DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture);
            var totalBytes = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
            results.Add(new DailyUsage(day, totalBytes / 1024.0 / 1024.0));
        }

        return results;
    }

    /// <summary>Top N largest files written/changed today.</summary>
    public static List<FileRecord> GetTopFilesToday(int topN = 10)
        => GetTopFilesInRange(DateTime.Today, DateTime.Today.AddDays(1).AddTicks(-1), topN);

    /// <summary>
    /// Top N largest files within [from, to]. Results are deduplicated by FilePath (keeping the
    /// largest observed size in the window) so a single file that was rewritten many times
    /// (logs, VM disks, pagefile, etc.) doesn't flood the list with copies of itself.
    /// </summary>
    public static List<FileRecord> GetTopFilesInRange(DateTime from, DateTime to, int topN = 10)
    {
        var results = new List<FileRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                MAX(Id) AS Id,
                FilePath,
                FileName,
                Extension,
                MAX(SizeBytes) AS SizeBytes,
                MAX(IsSystemFile) AS IsSystemFile,
                MAX(Timestamp) AS Timestamp
            FROM FileHistory
            WHERE Timestamp BETWEEN $from AND $to
            GROUP BY FilePath
            ORDER BY SizeBytes DESC
            LIMIT $top;
            """;
        command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$top", topN);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFileRecord(reader));
        }

        return results;
    }

    /// <summary>Top N largest files whose recorded size is at least <paramref name="minSizeBytes"/>, deduplicated by path. No date filter.</summary>
    public static List<FileRecord> GetTopFilesAboveSize(long minSizeBytes, int topN = 10)
    {
        var results = new List<FileRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                MAX(Id) AS Id,
                FilePath,
                FileName,
                Extension,
                MAX(SizeBytes) AS SizeBytes,
                MAX(IsSystemFile) AS IsSystemFile,
                MAX(Timestamp) AS Timestamp
            FROM FileHistory
            WHERE SizeBytes >= $minSize
            GROUP BY FilePath
            ORDER BY SizeBytes DESC
            LIMIT $top;
            """;
        command.Parameters.AddWithValue("$minSize", minSizeBytes);
        command.Parameters.AddWithValue("$top", topN);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFileRecord(reader));
        }

        return results;
    }

    /// <summary>Deletes rows older than <paramref name="days"/> days from FileHistory and FolderMap. Returns rows removed per table.</summary>
    public static (long FileRows, long ScanRows) PruneOlderThan(int days)
    {
        lock (WriteLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$cutoff", $"-{days} days");

            command.CommandText = "DELETE FROM FileHistory WHERE Timestamp < datetime('now', 'localtime', $cutoff);";
            long fileRows = command.ExecuteNonQuery();

            command.CommandText = "DELETE FROM FolderMap WHERE ScannedAt < datetime('now', 'localtime', $cutoff);";
            long scanRows = command.ExecuteNonQuery();

            return (fileRows, scanRows);
        }
    }

    public static int DeleteFileHistoryRows(IReadOnlyCollection<long> ids) => DeleteRowsById("FileHistory", ids);

    public static int DeleteFolderMapRows(IReadOnlyCollection<long> ids) => DeleteRowsById("FolderMap", ids);

    private static int DeleteRowsById(string table, IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        lock (WriteLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            var placeholders = new List<string>();
            foreach (var id in ids)
            {
                string name = $"$id{placeholders.Count}";
                placeholders.Add(name);
                command.Parameters.AddWithValue(name, id);
            }

            command.CommandText = $"DELETE FROM {table} WHERE Id IN ({string.Join(", ", placeholders)});";
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>Filtered FileHistory rows, newest first. Every filled-in field must match (AND).</summary>
    public static List<FileRecord> QueryFileHistory(FileHistoryFilter filter, int limit = 50)
    {
        var clauses = new List<string>();
        var results = new List<FileRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        if (filter.Id is { } id)
        {
            clauses.Add("Id = $id");
            command.Parameters.AddWithValue("$id", id);
        }
        if (!string.IsNullOrWhiteSpace(filter.NameContains))
        {
            clauses.Add("FileName LIKE $name");
            command.Parameters.AddWithValue("$name", $"%{filter.NameContains.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(filter.Extension))
        {
            string ext = filter.Extension.Trim();
            clauses.Add("Extension = $ext COLLATE NOCASE");
            command.Parameters.AddWithValue("$ext", ext.StartsWith('.') ? ext : "." + ext);
        }
        if (filter.MinSizeMb is { } minMb)
        {
            clauses.Add("SizeBytes >= $minSize");
            command.Parameters.AddWithValue("$minSize", (long)(minMb * 1024 * 1024));
        }
        if (filter.From is { } from)
        {
            clauses.Add("Timestamp >= $from");
            command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
        if (filter.To is { } to)
        {
            clauses.Add("Timestamp <= $to");
            command.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
        if (filter.IsSystemFile is { } isSystem)
        {
            clauses.Add("IsSystemFile = $isSys");
            command.Parameters.AddWithValue("$isSys", isSystem ? 1 : 0);
        }

        string where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        command.CommandText = $"""
            SELECT Id, FilePath, FileName, Extension, SizeBytes, IsSystemFile, Timestamp
            FROM FileHistory
            {where}
            ORDER BY Timestamp DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFileRecord(reader));
        }

        return results;
    }

    /// <summary>Filtered FolderMap rows, newest first. Every filled-in field must match (AND).</summary>
    public static List<FolderMapEntry> QueryFolderMap(FolderMapFilter filter, int limit = 50)
    {
        var clauses = new List<string>();
        var results = new List<FolderMapEntry>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        if (filter.Id is { } id)
        {
            clauses.Add("Id = $id");
            command.Parameters.AddWithValue("$id", id);
        }
        if (!string.IsNullOrWhiteSpace(filter.ScanId))
        {
            clauses.Add("ScanId = $scanId");
            command.Parameters.AddWithValue("$scanId", filter.ScanId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(filter.PathContains))
        {
            clauses.Add("FolderPath LIKE $path");
            command.Parameters.AddWithValue("$path", $"%{filter.PathContains.Trim()}%");
        }
        if (filter.MinSizeMb is { } minMb)
        {
            clauses.Add("TotalSizeBytes >= $minSize");
            command.Parameters.AddWithValue("$minSize", (long)(minMb * 1024 * 1024));
        }

        string where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        command.CommandText = $"""
            SELECT Id, ScanId, FolderPath, ParentPath, TotalSizeBytes, FileCount, SubfolderCount, IsSystemFolder, Description, ScannedAt
            FROM FolderMap
            {where}
            ORDER BY ScannedAt DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FolderMapEntry(
                ScanId: reader.GetString(1),
                FolderPath: reader.GetString(2),
                ParentPath: reader.GetString(3),
                TotalSizeBytes: reader.GetInt64(4),
                FileCount: reader.GetInt32(5),
                SubfolderCount: reader.GetInt32(6),
                IsSystemFolder: reader.GetInt64(7) == 1,
                Description: reader.GetString(8),
                ScannedAt: DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                Id: reader.GetInt64(0)));
        }

        return results;
    }

    /// <summary>All file history rows recorded in the current calendar month (used for folder roll-ups).</summary>
    public static List<FileRecord> GetCurrentMonthRecords()
    {
        var results = new List<FileRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, FilePath, FileName, Extension, SizeBytes, IsSystemFile, Timestamp
            FROM FileHistory
            WHERE strftime('%Y-%m', Timestamp) = strftime('%Y-%m', 'now');
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadFileRecord(reader));
        }

        return results;
    }

    private static FileRecord ReadFileRecord(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        FilePath: reader.GetString(1),
        FileName: reader.GetString(2),
        Extension: reader.GetString(3),
        SizeBytes: reader.GetInt64(4),
        IsSystemFile: reader.GetInt64(5) == 1,
        Timestamp: DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture));

    /// <summary>Thread-safe insert used by the Map Out drive scanner. Only call for folders at/above the chosen size threshold.</summary>
    public static void InsertFolderMapEntry(
        string scanId, string folderPath, string parentPath, long totalSizeBytes,
        int fileCount, int subfolderCount, bool isSystemFolder, string description)
    {
        lock (WriteLock)
        {
            try
            {
                using var connection = OpenConnection();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO FolderMap (ScanId, FolderPath, ParentPath, TotalSizeBytes, FileCount, SubfolderCount, IsSystemFolder, Description, ScannedAt)
                    VALUES ($scanId, $path, $parent, $size, $files, $subfolders, $isSys, $desc, $ts);
                    """;
                command.Parameters.AddWithValue("$scanId", scanId);
                command.Parameters.AddWithValue("$path", folderPath);
                command.Parameters.AddWithValue("$parent", parentPath);
                command.Parameters.AddWithValue("$size", totalSizeBytes);
                command.Parameters.AddWithValue("$files", fileCount);
                command.Parameters.AddWithValue("$subfolders", subfolderCount);
                command.Parameters.AddWithValue("$isSys", isSystemFolder ? 1 : 0);
                command.Parameters.AddWithValue("$desc", description);
                command.Parameters.AddWithValue("$ts", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            catch (SqliteException)
            {
                // Same reasoning as InsertFileRecord: never let a single row failure abort a long scan.
            }
        }
    }

    /// <summary>Top N largest folders recorded for a given scan.</summary>
    public static List<FolderMapEntry> GetTopFoldersForScan(string scanId, int topN = 25)
    {
        var results = new List<FolderMapEntry>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ScanId, FolderPath, ParentPath, TotalSizeBytes, FileCount, SubfolderCount, IsSystemFolder, Description, ScannedAt
            FROM FolderMap
            WHERE ScanId = $scanId
            ORDER BY TotalSizeBytes DESC
            LIMIT $top;
            """;
        command.Parameters.AddWithValue("$scanId", scanId);
        command.Parameters.AddWithValue("$top", topN);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FolderMapEntry(
                ScanId: reader.GetString(0),
                FolderPath: reader.GetString(1),
                ParentPath: reader.GetString(2),
                TotalSizeBytes: reader.GetInt64(3),
                FileCount: reader.GetInt32(4),
                SubfolderCount: reader.GetInt32(5),
                IsSystemFolder: reader.GetInt64(6) == 1,
                Description: reader.GetString(7),
                ScannedAt: DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture)));
        }

        return results;
    }

    /// <summary>Most recent scan's identifier, or null if no scan has ever run.</summary>
    public static string? GetLatestScanId()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ScanId FROM FolderMap ORDER BY ScannedAt DESC LIMIT 1;";
        return command.ExecuteScalar() as string;
    }

    /// <summary>Up to <paramref name="count"/> most recent distinct Map Out scans, newest first.</summary>
    public static List<(string ScanId, DateTime ScannedAt)> GetRecentScanIds(int count = 10)
    {
        var results = new List<(string, DateTime)>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ScanId, MAX(ScannedAt) AS LatestRow
            FROM FolderMap
            GROUP BY ScanId
            ORDER BY LatestRow DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$count", count);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture)));
        }

        return results;
    }

    /// <summary>All folder rows for one scan, keyed by folder path — used for before/after growth comparisons.</summary>
    public static Dictionary<string, FolderMapEntry> GetFolderMapForScan(string scanId)
    {
        var results = new Dictionary<string, FolderMapEntry>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ScanId, FolderPath, ParentPath, TotalSizeBytes, FileCount, SubfolderCount, IsSystemFolder, Description, ScannedAt
            FROM FolderMap
            WHERE ScanId = $scanId;
            """;
        command.Parameters.AddWithValue("$scanId", scanId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var entry = ReadFolderMapEntry(reader);
            results[entry.FolderPath] = entry;
        }

        return results;
    }

    private static FolderMapEntry ReadFolderMapEntry(SqliteDataReader reader) => new(
        ScanId: reader.GetString(0),
        FolderPath: reader.GetString(1),
        ParentPath: reader.GetString(2),
        TotalSizeBytes: reader.GetInt64(3),
        FileCount: reader.GetInt32(4),
        SubfolderCount: reader.GetInt32(5),
        IsSystemFolder: reader.GetInt64(6) == 1,
        Description: reader.GetString(7),
        ScannedAt: DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture));

    /// <summary>
    /// Wipes every row from FileHistory ("Database 1" — the live write/history log) and
    /// reclaims the freed space with VACUUM. Returns the number of rows deleted.
    /// </summary>
    public static long ClearFileHistory()
    {
        lock (WriteLock)
        {
            using var connection = OpenConnection();
            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM FileHistory;";
            long rows = Convert.ToInt64(countCommand.ExecuteScalar());

            using var deleteCommand = connection.CreateCommand();
            deleteCommand.CommandText = "DELETE FROM FileHistory;";
            deleteCommand.ExecuteNonQuery();

            using var vacuumCommand = connection.CreateCommand();
            vacuumCommand.CommandText = "VACUUM;";
            vacuumCommand.ExecuteNonQuery();

            return rows;
        }
    }

    /// <summary>
    /// Wipes every row from FolderMap ("Database 2" — the Map Out folder inventory) and
    /// reclaims the freed space with VACUUM. Returns the number of rows deleted.
    /// </summary>
    public static long ClearFolderMap()
    {
        lock (WriteLock)
        {
            using var connection = OpenConnection();
            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = "SELECT COUNT(*) FROM FolderMap;";
            long rows = Convert.ToInt64(countCommand.ExecuteScalar());

            using var deleteCommand = connection.CreateCommand();
            deleteCommand.CommandText = "DELETE FROM FolderMap;";
            deleteCommand.ExecuteNonQuery();

            using var vacuumCommand = connection.CreateCommand();
            vacuumCommand.CommandText = "VACUUM;";
            vacuumCommand.ExecuteNonQuery();

            return rows;
        }
    }
}

// =============================================================================
// CLASSIFICATION ENGINE
// =============================================================================

/// <summary>Decides whether a given file is a protected OS/system file or safe user data.</summary>
internal static class FileClassifier
{
    private static readonly string[] SystemDirectoryMarkers =
    {
        @"\windows\",
        @"\programdata\microsoft\",
        @"\program files\windowsapps\",
        @"\$recycle.bin\",
        @"\system volume information\",
        @"\msocache\",
    };

    private static readonly HashSet<string> SystemExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sys", ".dll", ".drv", ".ocx", ".cpl", ".mui", ".efi",
    };

    public static bool IsSystemFile(string fullPath, string extension)
    {
        var normalizedPath = fullPath.Replace('/', '\\').ToLowerInvariant();

        if (SystemDirectoryMarkers.Any(marker => normalizedPath.Contains(marker, StringComparison.Ordinal)))
        {
            return true;
        }

        return SystemExtensions.Contains(extension);
    }

    /// <summary>Same protected-directory rules as <see cref="IsSystemFile"/>, applied to a folder path.</summary>
    public static bool IsSystemFolder(string fullPath)
    {
        var normalizedPath = fullPath.Replace('/', '\\').ToLowerInvariant();
        if (!normalizedPath.EndsWith('\\'))
        {
            normalizedPath += '\\';
        }

        return SystemDirectoryMarkers.Any(marker => normalizedPath.Contains(marker, StringComparison.Ordinal));
    }
}

/// <summary>Best-effort, pattern-based description for well-known folder types found during a Map Out scan.</summary>
internal static class FolderDescriptionLookup
{
    // Order matters: first matching pattern wins, so put more specific patterns first.
    private static readonly (string Pattern, string Description)[] KnownPatterns =
    {
        (@"\winsxs", "Windows Component Store (OS update backups)"),
        (@"\softwaredistribution", "Windows Update staging/download cache"),
        (@"\driverstore", "Windows driver store"),
        (@"\windows\temp", "Windows temporary files"),
        (@"\appdata\local\temp", "User temporary files"),
        (@"\programdata\microsoft\windows defender", "Windows Defender data/definitions"),
        (@"\programdata\microsoft\windows\wer", "Windows Error Reporting crash dumps"),
        (@"\node_modules", "Node.js dependencies"),
        (@"\.git", "Git repository data"),
        (@"\appdata\local\docker", "Docker virtual disk/data"),
        (@".vhdx", "Virtual disk (Docker/WSL/Hyper-V)"),
        (@"\appdata\local\packages", "Windows Store app sandboxed data"),
        (@"\user data", "Browser profile/cache data"),
        (@"\indexeddb", "Browser IndexedDB storage"),
        (@"\service worker", "Browser Service Worker cache"),
        (@"\cache", "Cache data"),
        (@"\logs", "Application log files"),
        (@"\downloads", "User downloads"),
        (@"\workspacestorage", "Editor workspace metadata/cache"),
        (@"\globalstorage", "Editor global extension storage"),
        (@"\$recycle.bin", "Recycle Bin contents"),
        (@"\system volume information", "System restore points/shadow copies"),
    };

    public static string Describe(string fullPath)
    {
        var normalized = fullPath.Replace('/', '\\').ToLowerInvariant();
        foreach (var (pattern, description) in KnownPatterns)
        {
            if (normalized.Contains(pattern, StringComparison.Ordinal))
            {
                return description;
            }
        }

        return string.Empty;
    }
}

// =============================================================================
// WRITE ACTIVITY RADAR (churn vs. burst detection)
// =============================================================================

/// <summary>
/// Tracks every write seen by the watcher (no 5 MB floor — small, rapid writes are exactly
/// what churn detection needs) over a trailing time window, per parent folder. Computes two
/// independent signals so you can see *why* a folder is hot:
///   - Churn:  many small writes, fast           (events/minute)
///   - Burst:  a lot of bytes written, fast       (MB/minute)
/// </summary>
internal sealed class WriteActivityTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // Sensible defaults — tune here if your drive is naturally noisier/quieter.
    private const double ChurnEventsPerMinuteThreshold = 20;
    private const double BurstMbPerMinuteThreshold = 50;

    private readonly ConcurrentDictionary<string, long> _lastKnownSize = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string Folder, long DeltaBytes, DateTime Timestamp)> _events = new();
    private readonly object _trimLock = new();
    private readonly DateTime _startedAt = DateTime.UtcNow;

    /// <summary>Records a write observation. Call this for every event, regardless of file size.</summary>
    public void Record(string fullPath, long currentSize, bool isNewFile)
    {
        long delta;
        if (isNewFile)
        {
            delta = currentSize; // brand-new file: the whole thing just got written
        }
        else if (_lastKnownSize.TryGetValue(fullPath, out var previousSize))
        {
            delta = Math.Max(0, currentSize - previousSize);
        }
        else
        {
            delta = 0; // first sighting of a pre-existing file: establish a baseline, don't count it
        }

        _lastKnownSize[fullPath] = currentSize;

        if (delta <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        string folder = Path.GetDirectoryName(fullPath) ?? fullPath;
        _events.Enqueue((folder, delta, now));
        TrimOldEvents(now);
    }

    private void TrimOldEvents(DateTime now)
    {
        lock (_trimLock)
        {
            while (_events.TryPeek(out var oldest) && now - oldest.Timestamp > Window)
            {
                _events.TryDequeue(out _);
            }
        }
    }

    /// <summary>Top hot folders in the current window, ranked by churn and burst combined.</summary>
    public List<FolderActivity> GetHotFolders(int top = 5)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - Window;
        double elapsedMinutes = Math.Max(0.25, Math.Min(Window.TotalMinutes, (now - _startedAt).TotalMinutes));

        var snapshot = _events.ToArray().Where(e => e.Timestamp >= cutoff);

        var activity = snapshot
            .GroupBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                int count = g.Count();
                double totalMb = g.Sum(e => e.DeltaBytes) / 1024.0 / 1024.0;
                double eventsPerMin = count / elapsedMinutes;
                double mbPerMin = totalMb / elapsedMinutes;
                return new FolderActivity(
                    FolderPath: g.Key,
                    WriteCount: count,
                    EventsPerMinute: eventsPerMin,
                    MbPerMinute: mbPerMin,
                    IsChurnAlert: eventsPerMin >= ChurnEventsPerMinuteThreshold,
                    IsBurstAlert: mbPerMin >= BurstMbPerMinuteThreshold);
            })
            .OrderByDescending(f => f.IsBurstAlert || f.IsChurnAlert)
            .ThenByDescending(f => f.MbPerMinute + f.EventsPerMinute)
            .Take(top)
            .ToList();

        return activity;
    }
}

// =============================================================================
// REAL-TIME MONITOR ENGINE
// =============================================================================

/// <summary>Wraps a FileSystemWatcher, applies the size/classification filters, and persists results.</summary>
internal sealed class DiskWatcherService : IDisposable
{
    private const long MinimumSizeBytes = 5L * 1024 * 1024; // 5 MB floor for history persistence
    private const int DebounceWindowSeconds = 3;

    private readonly string _rootPath;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _recentEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _activityLog = new();
    private readonly WriteActivityTracker _activityTracker = new();

    public int FilesLoggedCount;
    public int ErrorsSuppressedCount;
    public string RootPath => _rootPath;

    public DiskWatcherService(string rootPath)
    {
        _rootPath = rootPath;
        _watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter = "*.*",
            InternalBufferSize = 64 * 1024, // reduce dropped-event risk on a busy drive
        };

        _watcher.Created += (_, e) => OnFileEvent(e.FullPath, isCreated: true);
        _watcher.Changed += (_, e) => OnFileEvent(e.FullPath, isCreated: false);
        _watcher.Error += (_, e) => LogActivity($"[yellow]Watcher buffer error:[/] {e.GetException().Message}");
    }

    /// <summary>Top hot folders (churn/burst) over the trailing 5-minute window — for manual cleanup, not auto-delete.</summary>
    public List<FolderActivity> GetHotFolders(int top = 5) => _activityTracker.GetHotFolders(top);

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        LogActivity($"[green]Monitoring started[/] on [bold]{_rootPath}[/] (history logs files >= 5 MB; the Hot Write Paths radar watches all sizes).");
    }

    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
        LogActivity("[red]Monitoring stopped.[/]");
    }

    public IReadOnlyCollection<string> DrainRecentActivity(int max = 8)
    {
        var buffer = new List<string>();
        while (buffer.Count < max && _activityLog.TryDequeue(out var entry))
        {
            buffer.Add(entry);
        }
        return buffer;
    }

    private void OnFileEvent(string fullPath, bool isCreated)
    {
        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                return; // deleted/renamed before we could inspect it
            }

            long sizeBytes = fileInfo.Length;

            // Feed the write-activity radar with EVERY write, no size floor — churn
            // detection needs the small, rapid writes that forensic history ignores.
            _activityTracker.Record(fullPath, sizeBytes, isCreated);

            // Debounce: Changed/Created can fire multiple times for the same write burst.
            // This only gates the (noisier) forensic-history persistence path below.
            var now = DateTime.UtcNow;
            if (_recentEvents.TryGetValue(fullPath, out var lastSeen) &&
                (now - lastSeen).TotalSeconds < DebounceWindowSeconds)
            {
                return;
            }
            _recentEvents[fullPath] = now;

            if (sizeBytes < MinimumSizeBytes)
            {
                return;
            }

            string extension = string.IsNullOrEmpty(fileInfo.Extension) ? string.Empty : fileInfo.Extension;
            bool isSystem = FileClassifier.IsSystemFile(fullPath, extension);

            DiskHistoryDatabase.InsertFileRecord(fullPath, fileInfo.Name, extension, sizeBytes, isSystem);
            Interlocked.Increment(ref FilesLoggedCount);

            string tag = isSystem ? "[red]SYSTEM[/]" : "[green]SAFE[/]";
            LogActivity($"[grey]{DateTime.Now:HH:mm:ss}[/] {tag} {EscapeMarkup(fileInfo.Name)} ({sizeBytes / 1024.0 / 1024.0:F1} MB)");
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref ErrorsSuppressedCount); // permission-denied, ignore
        }
        catch (IOException)
        {
            Interlocked.Increment(ref ErrorsSuppressedCount); // file locked by another process, ignore
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref ErrorsSuppressedCount);
            LogActivity($"[yellow]Suppressed error:[/] {EscapeMarkup(ex.Message)}");
        }
    }

    private void LogActivity(string message)
    {
        _activityLog.Enqueue(message);
        while (_activityLog.Count > 50 && _activityLog.TryDequeue(out _))
        {
            // keep the queue bounded
        }
    }

    private static string EscapeMarkup(string text) => text.Replace("[", "[[").Replace("]", "]]");

    public void Dispose() => _watcher.Dispose();
}

// =============================================================================
// MAP OUT ENGINE (one-time/on-demand folder inventory scan)
// =============================================================================

/// <summary>
/// Walks a directory tree bottom-up, computing each folder's total recursive size,
/// and persists every folder at/above a size threshold as a dated "scan" snapshot.
/// This is a point-in-time inventory — unlike the live watcher, it sees bloat that
/// already exists on disk, not just new writes.
/// </summary>
internal static class DiskMapper
{
    /// <summary>Scans <paramref name="rootPath"/> and stores every folder >= <paramref name="thresholdBytes"/> under a new ScanId.</summary>
    /// <returns>The ScanId generated for this run, and how many folders were stored.</returns>
    public static (string ScanId, int FoldersMapped, int FoldersSkipped) Scan(
        string rootPath, long thresholdBytes, Action<string, int> onFolderMapped)
    {
        string scanId = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        int foldersMapped = 0;
        int foldersSkipped = 0;

        ScanDirectory(rootPath, scanId, thresholdBytes, ref foldersMapped, ref foldersSkipped, onFolderMapped);

        return (scanId, foldersMapped, foldersSkipped);
    }

    private static long ScanDirectory(
        string path, string scanId, long thresholdBytes,
        ref int foldersMapped, ref int foldersSkipped, Action<string, int> onFolderMapped)
    {
        long totalSize = 0;
        int fileCount = 0;
        int subfolderCount = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                try
                {
                    totalSize += new FileInfo(file).Length;
                    fileCount++;
                }
                catch (UnauthorizedAccessException) { /* locked/permission-denied file — skip it */ }
                catch (IOException) { /* vanished/locked file — skip it */ }
            }

            foreach (var subDir in Directory.EnumerateDirectories(path))
            {
                subfolderCount++;
                totalSize += ScanDirectory(subDir, scanId, thresholdBytes, ref foldersMapped, ref foldersSkipped, onFolderMapped);
            }
        }
        catch (UnauthorizedAccessException)
        {
            Interlocked.Increment(ref foldersSkipped); // protected folder — can't see inside it, keep going
        }
        catch (IOException)
        {
            Interlocked.Increment(ref foldersSkipped); // transient I/O error enumerating this folder — keep going
        }

        if (totalSize >= thresholdBytes)
        {
            string parentPath = Path.GetDirectoryName(path) ?? string.Empty;
            bool isSystem = FileClassifier.IsSystemFolder(path);
            string description = FolderDescriptionLookup.Describe(path);

            DiskHistoryDatabase.InsertFolderMapEntry(scanId, path, parentPath, totalSize, fileCount, subfolderCount, isSystem, description);
            Interlocked.Increment(ref foldersMapped);
            onFolderMapped(path, foldersMapped);
        }

        return totalSize;
    }
}

// =============================================================================
// REPORTING ENGINE (Spectre.Console renderers)
// =============================================================================

internal static class ReportRenderer
{
    public static void RenderRollingHistory()
    {
        var usage = DiskHistoryDatabase.GetRollingDailyUsage(15);

        if (usage.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No history recorded yet. Start background tracking first.[/]");
            return;
        }

        var chart = new BarChart()
            .Width(70)
            .Label("[bold underline]15-Day Rolling Disk Write History (MB/day)[/]")
            .CenterLabel();

        foreach (var day in usage)
        {
            var color = day.TotalMb switch
            {
                > 1024 => Color.Red,
                > 256 => Color.Yellow,
                _ => Color.Green,
            };
            chart.AddItem(day.Day.ToString("MM-dd", CultureInfo.InvariantCulture), Math.Round(day.TotalMb, 1), color);
        }

        AnsiConsole.Write(new Panel(chart).Header("[bold cyan] Disk Activity Trend [/]").Expand());
    }

    public static void RenderTopFilesAboveSize(long minSizeBytes)
    {
        var files = DiskHistoryDatabase.GetTopFilesAboveSize(minSizeBytes, 10);

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[bold]ID[/]").RightAligned());
        table.AddColumn("[bold]File Name[/]");
        table.AddColumn(new TableColumn("[bold]Size (MB)[/]").RightAligned());
        table.AddColumn("[bold]Path[/]");
        table.AddColumn("[bold]Safety Tag[/]");

        if (files.Count == 0)
        {
            table.AddRow("[grey]--[/]", "[grey]--[/]", "[grey]--[/]", "[grey]No recorded files at or above this size yet.[/]", "[grey]--[/]");
        }

        foreach (var file in files)
        {
            string tag = file.IsSystemFile
                ? "[red] Windows OS System File[/]"
                : "[green] Safe to Delete[/]";

            table.AddRow(
                file.Id.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(file.FileName),
                file.SizeMb.ToString("F2", CultureInfo.InvariantCulture),
                Markup.Escape(file.FilePath),
                tag);
        }

        string header = $"[bold cyan] Top 10 Largest Files — at least {minSizeBytes / (1024.0 * 1024.0):F0} MB [/]";

        AnsiConsole.Write(new Panel(table).Header(header).Expand());
    }

    public static void RenderMonthlyFolderAccumulation()
    {
        var records = DiskHistoryDatabase.GetCurrentMonthRecords();

        if (records.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No history recorded this month yet. Start background tracking first.[/]");
            return;
        }

        var folderGroups = records
            .GroupBy(r => Path.GetDirectoryName(r.FilePath) ?? r.FilePath)
            .Select(g => new FolderAccumulation(
                FolderPath: g.Key,
                TotalMb: g.Sum(r => r.SizeMb),
                SystemFileCount: g.Count(r => r.IsSystemFile),
                SafeFileCount: g.Count(r => !r.IsSystemFile)))
            .OrderByDescending(f => f.TotalMb)
            .Take(5)
            .ToList();

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[bold]Rank[/]");
        table.AddColumn("[bold]Folder[/]");
        table.AddColumn(new TableColumn("[bold]Total (MB)[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]System Files[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Safe Files[/]").Centered());

        int rank = 1;
        foreach (var folder in folderGroups)
        {
            table.AddRow(
                $"#{rank++}",
                Markup.Escape(folder.FolderPath),
                folder.TotalMb.ToString("F2", CultureInfo.InvariantCulture),
                $"[red]{folder.SystemFileCount}[/]",
                $"[green]{folder.SafeFileCount}[/]");
        }

        AnsiConsole.Write(new Panel(table)
            .Header($"[bold cyan] Monthly Folder Accumulation — {DateTime.Now:MMMM yyyy} (Top 5) [/]")
            .Expand());
    }

    public static void RenderFolderMap(string scanId, int foldersMapped, int foldersSkipped)
    {
        var folders = DiskHistoryDatabase.GetTopFoldersForScan(scanId, 25);

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[bold]Rank[/]");
        table.AddColumn("[bold]Folder[/]");
        table.AddColumn(new TableColumn("[bold]Size (MB)[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Files[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Subfolders[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Description[/]");

        if (folders.Count == 0)
        {
            table.AddRow("[grey]--[/]", "[grey]No folders matched the size threshold.[/]", "[grey]--[/]", "[grey]--[/]", "[grey]--[/]", "[grey]--[/]", "[grey]--[/]");
        }

        int rank = 1;
        foreach (var folder in folders)
        {
            string type = folder.IsSystemFolder ? "[red]❌ System[/]" : "[green]Safe[/]";
            string description = string.IsNullOrEmpty(folder.Description) ? "[grey]--[/]" : Markup.Escape(folder.Description);

            table.AddRow(
                $"#{rank++}",
                Markup.Escape(folder.FolderPath),
                folder.TotalSizeMb.ToString("F1", CultureInfo.InvariantCulture),
                folder.FileCount.ToString(CultureInfo.InvariantCulture),
                folder.SubfolderCount.ToString(CultureInfo.InvariantCulture),
                type,
                description);
        }

        AnsiConsole.Write(new Panel(table)
            .Header($"[bold cyan] Map Out Results — Scan {scanId} ({foldersMapped} folders mapped, {foldersSkipped} access-denied skipped) [/]")
            .Expand());
    }

    /// <summary>Raw dump of the FileHistory table — for debugging/inspection, no grouping or filtering.</summary>
    public static void RenderRawFileHistory(List<FileRecord> rows, int requestedLimit)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[bold]Id[/]");
        table.AddColumn("[bold]Timestamp[/]");
        table.AddColumn("[bold]File Name[/]");
        table.AddColumn("[bold]Ext[/]");
        table.AddColumn(new TableColumn("[bold]Size (MB)[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Path[/]");

        if (rows.Count == 0)
        {
            table.AddRow("[grey]--[/]", "[grey]--[/]", "[grey]No rows in FileHistory yet.[/]", "[grey]--[/]", "[grey]--[/]", "[grey]--[/]", "[grey]--[/]");
        }

        foreach (var row in rows)
        {
            table.AddRow(
                row.Id.ToString(CultureInfo.InvariantCulture),
                row.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Markup.Escape(row.FileName),
                Markup.Escape(row.Extension),
                row.SizeMb.ToString("F2", CultureInfo.InvariantCulture),
                row.IsSystemFile ? "[red]System[/]" : "[green]Safe[/]",
                Markup.Escape(row.FilePath));
        }

        AnsiConsole.Write(new Panel(table)
            .Header($"[bold cyan] Raw Table: FileHistory — {rows.Count} of up to {requestedLimit} most recent rows [/]")
            .Expand());
    }

    /// <summary>Raw dump of the FolderMap table across all scans — for debugging/inspection.</summary>
    public static void RenderRawFolderMap(List<FolderMapEntry> rows, int requestedLimit)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[bold]ID[/]").RightAligned());
        table.AddColumn("[bold]Scan[/]");
        table.AddColumn("[bold]Scanned At[/]");
        table.AddColumn("[bold]Folder[/]");
        table.AddColumn(new TableColumn("[bold]Size (MB)[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Files[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Subfolders[/]").RightAligned());
        table.AddColumn("[bold]Type[/]");

        if (rows.Count == 0)
        {
            table.AddRow("[grey]--[/]", "[grey]--[/]", "[grey]--[/]", "[grey]No rows match. Run Map Out first, or loosen the filters.[/]", "[grey]--[/]", "[grey]--[/]", "[grey]--[/]", "[grey]--[/]");
        }

        foreach (var row in rows)
        {
            string type = row.IsSystemFolder ? "[red]System[/]" : "[green]Safe[/]";
            table.AddRow(
                row.Id.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(row.ScanId),
                row.ScannedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Markup.Escape(row.FolderPath),
                row.TotalSizeMb.ToString("F1", CultureInfo.InvariantCulture),
                row.FileCount.ToString(CultureInfo.InvariantCulture),
                row.SubfolderCount.ToString(CultureInfo.InvariantCulture),
                type);
        }

        AnsiConsole.Write(new Panel(table)
            .Header($"[bold cyan] Raw Table: FolderMap — {rows.Count} of up to {requestedLimit} most recent rows [/]")
            .Expand());
    }

    /// <summary>
    /// Compares the two most recent Map Out scans folder-by-folder, showing which ones grew,
    /// shrunk, newly appeared, or dropped out since last time.
    /// </summary>
    public static void RenderFolderGrowth(string oldScanId, DateTime oldScannedAt, string newScanId, DateTime newScannedAt, List<FolderGrowthRow> rows)
    {
        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("[bold]Folder[/]");
        table.AddColumn(new TableColumn("[bold]Old (MB)[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]New (MB)[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Growth (MB)[/]").RightAligned());
        table.AddColumn("[bold]Status[/]");

        if (rows.Count == 0)
        {
            table.AddRow("[grey]No folders changed between these two scans.[/]", "[grey]--[/]", "[grey]--[/]", "[grey]--[/]", "[grey]--[/]");
        }

        foreach (var row in rows)
        {
            string statusTag = row.Status switch
            {
                "NEW" => "[yellow]New folder[/]",
                "REMOVED" => "[grey]Gone / below threshold[/]",
                "GROWN" => "[red]Grew[/]",
                "SHRUNK" => "[green]Shrunk[/]",
                _ => "[grey]--[/]",
            };

            string deltaText = row.DeltaMb switch
            {
                > 0 => $"[red]+{row.DeltaMb:F1}[/]",
                < 0 => $"[green]{row.DeltaMb:F1}[/]",
                _ => "0.0",
            };

            table.AddRow(
                Markup.Escape(row.FolderPath),
                row.OldSizeMb.ToString("F1", CultureInfo.InvariantCulture),
                row.NewSizeMb.ToString("F1", CultureInfo.InvariantCulture),
                deltaText,
                statusTag);
        }

        AnsiConsole.Write(new Panel(table)
            .Header($"[bold cyan] Folder Growth — {oldScannedAt:yyyy-MM-dd HH:mm} (scan {oldScanId}) -> {newScannedAt:yyyy-MM-dd HH:mm} (scan {newScanId}) [/]")
            .Expand());
    }
}

// =============================================================================
// PROGRAM ENTRY POINT
// =============================================================================

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--tray":
                    TrayHost.Run();
                    return;
                case "--install-startup":
                    Environment.ExitCode = StartupTask.Install();
                    return;
                case "--remove-startup":
                    Environment.ExitCode = StartupTask.Remove();
                    return;
            }
        }

        Console.Title = "DiskViator";

        var travelerIcon = new Markup(
            "[yellow]   __[/]\n" +
            "[yellow] .'  '.[/]\n" +
            "[grey93]/______\\[/]\n" +
            "[tan] (o  o) [/]\n" +
            "[tan]  \\__/  [/]\n" +
            "[yellow] /|[/][white]  [/][yellow]|\\ [/]\n" +
            "[yellow]  |  |  [/]");

        var titleRow = new Columns(travelerIcon, new FigletText("DiskViator").Color(Color.Cyan1))
        {
            Expand = false,
        };
        AnsiConsole.Write(Align.Center(titleRow));

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[white]Track large file writes, spot busy folders, and map existing disk usage.[/]");
        AnsiConsole.MarkupLine("[grey]History and largest-file reports summarize tracked writes. Drive maps inventory folders already on disk, while growth compares scans. Your data stays local; files are never deleted.[/]");
        AnsiConsole.WriteLine();

        try
        {
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start("Initializing database...", _ =>
                {
                    DiskHistoryDatabase.Initialize();
                    DiskHistoryDatabase.PruneOlderThan(DiskHistoryDatabase.RetentionDays);
                });
            AnsiConsole.MarkupLine($"[green]Database ready:[/] [grey]{Markup.Escape(DiskHistoryDatabase.DatabasePath)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal error initializing database:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        DiskWatcherService? watcherService = null;

        while (true)
        {
            AnsiConsole.WriteLine();
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What would you like to do?[/]")
                    .HighlightStyle(new Style(foreground: Color.Cyan1))
                    .AddChoices(
                        "1. Start Monitoring",
                        "2. View 15-Day Rolling History",
                        "3. Top 10 Largest Files (Min Size)",
                        "4. Monthly Folder Accumulation",
                        "5. Map Out Drive (Folder Inventory Instance)",
                        "6. Clean File History Log",
                        "7. Clean Folder Inventory",
                        "8. View & Filter Database Tables",
                        "9. Folder Growth Since Last Scan",
                        "10. Exit",
                        "11. Setup Tray Monitoring (Folders)"));

            try
            {
                string menuNumber = choice.Split('.', 2)[0];
                switch (menuNumber)
                {
                    case "1":
                        watcherService = RunBackgroundTracking(watcherService);
                        break;
                    case "2":
                        ReportRenderer.RenderRollingHistory();
                        break;
                    case "3":
                        PromptAndRenderTopFilesAboveSize();
                        break;
                    case "4":
                        ReportRenderer.RenderMonthlyFolderAccumulation();
                        break;
                    case "5":
                        PromptAndRunMapOut();
                        break;
                    case "6":
                        PromptCleanMenu("FileHistory", "Database 1 (File History Log)", DiskHistoryDatabase.ClearFileHistory, DiskHistoryDatabase.DeleteFileHistoryRows);
                        break;
                    case "7":
                        PromptCleanMenu("FolderMap", "Database 2 (Folder Map Data)", DiskHistoryDatabase.ClearFolderMap, DiskHistoryDatabase.DeleteFolderMapRows);
                        break;
                    case "8":
                        PromptAndViewRawTables();
                        break;
                    case "9":
                        RenderFolderGrowthSinceLastScan();
                        break;
                    case "10":
                        watcherService?.Dispose();
                        AnsiConsole.MarkupLine("[grey]Goodbye.[/]");
                        return;
                    case "11":
                        ConfigureTrayMonitoring();
                        break;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {Markup.Escape(ex.Message)}");
            }
        }
    }

    /// <summary>
    /// Prompts for a root path and a size threshold, then runs a Map Out scan with a live
    /// progress status, and finally renders the resulting folder inventory table.
    /// </summary>
    private static void PromptAndRunMapOut()
    {
        string root = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Root folder to map[/] (e.g. C:\\):")
                .DefaultValue(@"C:\")
                .ValidationErrorMessage("[red]Path does not exist.[/]")
                .Validate(path => Directory.Exists(path)));

        double thresholdMb = AnsiConsole.Prompt(
            new TextPrompt<double>("[bold]Minimum folder size to store[/] (MB):")
                .DefaultValue(10.0)
                .ValidationErrorMessage("[red]Enter a non-negative number.[/]")
                .Validate(v => v >= 0));

        long thresholdBytes = (long)(thresholdMb * 1024 * 1024);

        AnsiConsole.MarkupLine($"[grey]Scanning {Markup.Escape(root)} — this can take a while on a full drive. Please wait...[/]");

        (string scanId, int mapped, int skipped) result = (string.Empty, 0, 0);

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Mapping out drive...", ctx =>
            {
                DateTime lastUiUpdate = DateTime.MinValue;

                result = DiskMapper.Scan(root, thresholdBytes, (currentPath, mappedCount) =>
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastUiUpdate).TotalMilliseconds < 150)
                    {
                        return; // throttle UI redraws so a huge scan doesn't choke on console writes
                    }
                    lastUiUpdate = now;
                    ctx.Status($"Mapping out drive... [bold]{mappedCount}[/] folders stored. Current: [grey]{Markup.Escape(currentPath)}[/]");
                });
            });

        AnsiConsole.MarkupLine(
            $"[green]Scan complete.[/] [bold]{result.mapped}[/] folders stored (scan [grey]{result.scanId}[/]), " +
            $"[grey]{result.skipped}[/] folders skipped (access denied).");

        ReportRenderer.RenderFolderMap(result.scanId, result.mapped, result.skipped);
    }

    /// <summary>
    /// Asks for confirmation, then wipes every row from the given table via <paramref name="clearAction"/>.
    /// Used for the "Clean Database 1/2" menu options — a full reset, not an age-based purge.
    /// </summary>
    private static void PromptAndCleanDatabase(string tableName, string label, Func<long> clearAction)
    {
        bool confirmed = AnsiConsole.Confirm(
            $"[red]This will permanently delete ALL rows from {Markup.Escape(tableName)} ({Markup.Escape(label)}).[/] Continue?",
            defaultValue: false);

        if (!confirmed)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled — no data was deleted.[/]");
            return;
        }

        long rowsDeleted = 0;
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start($"Wiping {tableName}...", _ => rowsDeleted = clearAction());

        AnsiConsole.MarkupLine($"[green]{Markup.Escape(label)} cleared.[/] [bold]{rowsDeleted}[/] rows deleted, space reclaimed.");
    }

    /// <summary>Clean submenu for a table: delete chosen rows by ID, wipe the whole table, or go back.</summary>
    private static void PromptCleanMenu(
        string tableName,
        string label,
        Func<long> clearAction,
        Func<IReadOnlyCollection<long>, int> deleteByIdAction)
    {
        string choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold]{Markup.Escape(label)}[/] — what do you want to do?")
                .HighlightStyle(new Style(foreground: Color.Cyan1))
                .AddChoices(
                    "Delete specific rows by ID",
                    "Wipe the entire table",
                    "Back"));

        switch (choice)
        {
            case "Delete specific rows by ID":
                PromptAndDeleteById(tableName, label, deleteByIdAction);
                break;
            case "Wipe the entire table":
                PromptAndCleanDatabase(tableName, label, clearAction);
                break;
        }
    }

    /// <summary>Deletes the rows whose IDs the user lists (comma-separated), after confirmation.</summary>
    private static void PromptAndDeleteById(string tableName, string label, Func<IReadOnlyCollection<long>, int> deleteByIdAction)
    {
        string input = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Enter ID(s) to delete[/] (comma-separated, e.g. 4, 17, 20):")
                .Validate(text => ParseIds(text) is { Count: > 0 }
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Enter one or more positive whole-number IDs.[/]")));

        var ids = ParseIds(input)!;

        bool confirmed = AnsiConsole.Confirm(
            $"[red]Delete {ids.Count} row(s) with ID {Markup.Escape(string.Join(", ", ids))} from {Markup.Escape(tableName)}?[/]",
            defaultValue: false);

        if (!confirmed)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled — no data was deleted.[/]");
            return;
        }

        int rowsDeleted = deleteByIdAction(ids);
        AnsiConsole.MarkupLine($"[bold]{rowsDeleted}[/] row(s) deleted from {Markup.Escape(label)}.");
    }

    /// <summary>Lets the user pick a raw table, optionally filter it (ID, text, size, dates), and show the newest rows.</summary>
    private static void PromptAndViewRawTables()
    {
        string tableChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Which table do you want to view?[/]")
                .HighlightStyle(new Style(foreground: Color.Cyan1))
                .AddChoices(
                    "FileHistory (Database 1 — file write/history log)",
                    "FolderMap (Database 2 — folder inventory scans)"));

        int limit = AnsiConsole.Prompt(
            new TextPrompt<int>("[bold]How many rows[/] (most recent first)?")
                .DefaultValue(50)
                .Validate(n => n > 0 ? ValidationResult.Success() : ValidationResult.Error("[red]Must be greater than 0.[/]")));

        if (tableChoice.StartsWith("FileHistory", StringComparison.Ordinal))
        {
            var rows = DiskHistoryDatabase.QueryFileHistory(PromptFileHistoryFilter(), limit);
            ReportRenderer.RenderRawFileHistory(rows, limit);
        }
        else
        {
            var rows = DiskHistoryDatabase.QueryFolderMap(PromptFolderMapFilter(), limit);
            ReportRenderer.RenderRawFolderMap(rows, limit);
        }
    }

    private static FileHistoryFilter PromptFileHistoryFilter()
    {
        AnsiConsole.MarkupLine("[grey]Leave any filter blank to skip it. All filled-in filters must match.[/]");

        long? id = PromptOptionalId();
        string? name = PromptOptionalText("Filename contains");
        string? extension = PromptOptionalText("Extension (e.g. .exe)");
        double? minMb = PromptOptionalMb("Minimum size (MB)");
        DateTime? fromDate = PromptOptionalDate("From date");
        DateTime? toDate = PromptOptionalDate("To date (inclusive)");

        // The To date covers the whole selected day, so extend it to 23:59:59.
        DateTime? to = toDate?.Date.AddDays(1).AddSeconds(-1);

        return new FileHistoryFilter(id, name, extension, minMb, fromDate?.Date, to);
    }

    private static FolderMapFilter PromptFolderMapFilter()
    {
        AnsiConsole.MarkupLine("[grey]Leave any filter blank to skip it. All filled-in filters must match.[/]");

        return new FolderMapFilter(
            Id: PromptOptionalId(),
            ScanId: PromptOptionalText("Scan ID"),
            PathContains: PromptOptionalText("Folder path contains"),
            MinSizeMb: PromptOptionalMb("Minimum folder size (MB)"));
    }

    private static long? PromptOptionalId()
    {
        string? text = OptionalResult(AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]ID[/] (optional):")
                .AllowEmpty()
                .Validate(t => string.IsNullOrWhiteSpace(t) || long.TryParse(t.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Enter a whole number.[/]"))));

        return text is null ? null : long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static string? PromptOptionalText(string label) =>
        OptionalResult(AnsiConsole.Prompt(new TextPrompt<string>($"[bold]{label}[/] (optional):").AllowEmpty()));

    private static double? PromptOptionalMb(string label)
    {
        string? text = OptionalResult(AnsiConsole.Prompt(
            new TextPrompt<string>($"[bold]{label}[/] (optional):")
                .AllowEmpty()
                .Validate(t => string.IsNullOrWhiteSpace(t) || (double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Enter a number of 0 or more.[/]"))));

        return text is null ? null : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static DateTime? PromptOptionalDate(string label)
    {
        string? text = OptionalResult(AnsiConsole.Prompt(
            new TextPrompt<string>($"[bold]{label}[/] yyyy-MM-dd (optional):")
                .AllowEmpty()
                .Validate(t => string.IsNullOrWhiteSpace(t) || ValidateDate(t)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Use yyyy-MM-dd, e.g. 2026-10-20.[/]"))));

        return text is null ? null : DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Compares the two most recent Map Out scans and reports which folders grew, shrunk,
    /// appeared, or disappeared since the previous scan.
    /// </summary>
    private static void RenderFolderGrowthSinceLastScan()
    {
        var recentScans = DiskHistoryDatabase.GetRecentScanIds(2);

        if (recentScans.Count < 2)
        {
            AnsiConsole.MarkupLine("[yellow]Need at least two Map Out scans to compare growth.[/] Run option 5 (Map Out Drive) twice, then try again.");
            return;
        }

        // Newest first from GetRecentScanIds — [0] is "new", [1] is "old".
        var (newScanId, newScannedAt) = recentScans[0];
        var (oldScanId, oldScannedAt) = recentScans[1];

        var oldMap = DiskHistoryDatabase.GetFolderMapForScan(oldScanId);
        var newMap = DiskHistoryDatabase.GetFolderMapForScan(newScanId);

        var allPaths = new HashSet<string>(oldMap.Keys, StringComparer.OrdinalIgnoreCase);
        allPaths.UnionWith(newMap.Keys);

        var growthRows = new List<FolderGrowthRow>();
        foreach (var path in allPaths)
        {
            bool hasOld = oldMap.TryGetValue(path, out var oldEntry);
            bool hasNew = newMap.TryGetValue(path, out var newEntry);

            long oldSize = hasOld ? oldEntry!.TotalSizeBytes : 0;
            long newSize = hasNew ? newEntry!.TotalSizeBytes : 0;

            string status = (hasOld, hasNew) switch
            {
                (false, true) => "NEW",
                (true, false) => "REMOVED",
                _ when newSize > oldSize => "GROWN",
                _ when newSize < oldSize => "SHRUNK",
                _ => "SAME",
            };

            if (status == "SAME")
            {
                continue;
            }

            growthRows.Add(new FolderGrowthRow(path, oldSize, newSize, status));
        }

        var topGrowth = growthRows
            .OrderByDescending(r => Math.Abs(r.DeltaMb))
            .Take(20)
            .ToList();

        ReportRenderer.RenderFolderGrowth(oldScanId, oldScannedAt, newScanId, newScannedAt, topGrowth);
    }

    /// <summary>
    /// Asks for a minimum file size in MB (default 500) and renders the largest recorded files at or above it.
    /// </summary>
    private static void PromptAndRenderTopFilesAboveSize()
    {
        double minMb = AnsiConsole.Prompt(
            new TextPrompt<double>("[bold]Minimum file size[/] (MB):")
                .DefaultValue(500.0)
                .ValidationErrorMessage("[red]Enter a non-negative number.[/]")
                .Validate(v => v >= 0 ? ValidationResult.Success() : ValidationResult.Error("[red]Must be 0 or greater.[/]")));

        ReportRenderer.RenderTopFilesAboveSize((long)(minMb * 1024 * 1024));
    }

    private static bool ValidateDate(string input) =>
        DateTime.TryParseExact(input.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Returns null when the user leaves the prompt blank, otherwise the trimmed text.</summary>
    private static string? OptionalResult(string input) =>
        string.IsNullOrWhiteSpace(input) ? null : input.Trim();

    /// <summary>Parses "1, 5, 12" into IDs. Returns null if any token is not a positive integer.</summary>
    private static List<long>? ParseIds(string input)
    {
        var ids = new List<long>();
        foreach (var token in input.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
            {
                return null;
            }
            ids.Add(id);
        }
        return ids.Distinct().ToList();
    }

    private static DiskWatcherService? RunBackgroundTracking(DiskWatcherService? existing)
    {
        if (existing is not null)
        {
            AnsiConsole.MarkupLine("[yellow]Tracking is already running in this session. Returning to live view.[/]");
        }
        else
        {
            string root = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter the drive/folder to monitor:")
                    .DefaultValue(@"C:\")
                    .ValidationErrorMessage("[red]Path does not exist.[/]")
                    .Validate(path => Directory.Exists(path)));

            try
            {
                existing = new DiskWatcherService(root);
                existing.Start();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Could not start watcher:[/] {Markup.Escape(ex.Message)}");
                return null;
            }
        }

        AnsiConsole.MarkupLine("[grey]Press [bold]Enter[/] at any time to return to the menu (tracking keeps running in the background).[/]");

        var watcher = existing;
        var recentLog = new List<string>();

        AnsiConsole.Live(BuildTrackingDashboard(watcher, recentLog))
            .Start(ctx =>
            {
                while (true)
                {
                    recentLog.AddRange(watcher.DrainRecentActivity());
                    while (recentLog.Count > 8)
                    {
                        recentLog.RemoveAt(0);
                    }

                    ctx.UpdateTarget(BuildTrackingDashboard(watcher, recentLog));

                    if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                    {
                        break;
                    }

                    Thread.Sleep(500);
                }
            });

        return existing;
    }

    private static void ConfigureTrayMonitoring()
    {
        AnsiConsole.MarkupLine("[grey]Choose one or more non-overlapping folders. Tray monitoring starts now and is scheduled at Windows logon.[/]");
        var rootPaths = new List<string>();

        do
        {
            string rootPath = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Folder to monitor in the tray[/]:")
                    .Validate(path =>
                    {
                        if (!Directory.Exists(path))
                        {
                            return ValidationResult.Error("[red]Folder does not exist.[/]");
                        }

                        try
                        {
                            TrayMonitoringSettings.ValidatePaths(rootPaths.Append(path));
                            return ValidationResult.Success();
                        }
                        catch (ArgumentException)
                        {
                            return ValidationResult.Error("[red]Folders cannot be duplicates or nested inside one another.[/]");
                        }
                    }));

            rootPaths.Add(Path.GetFullPath(rootPath));
        }
        while (AnsiConsole.Confirm("[bold]Add another folder?[/]", defaultValue: false));

        TrayMonitoringSettings.Save(rootPaths);
        AnsiConsole.MarkupLine($"[green]Tray folders saved:[/] {Markup.Escape(string.Join(", ", rootPaths))}");

        int startupResult = StartupTask.Install();
        if (startupResult != 0)
        {
            AnsiConsole.MarkupLine("[yellow]Tray monitoring will start now, but automatic logon startup was not enabled. Run setup again from an Administrator terminal to register it.[/]");
        }

        if (TrayHost.NotifyConfigurationChanged())
        {
            AnsiConsole.MarkupLine("[green]The running tray monitor has been updated with these folders.[/]");
        }
        else
        {
            TrayHost.StartDetached();
            AnsiConsole.MarkupLine("[green]Tray monitoring started in the notification area.[/]");
        }

        if (startupResult == 0)
        {
            AnsiConsole.MarkupLine("[green]Automatic tray monitoring is enabled for future logons.[/]");
        }
    }

    /// <summary>
    /// Builds the live tracking screen: a status line, the "Hot Write Paths" churn/burst
    /// radar table (for manual cleanup — nothing is auto-deleted), and a rolling activity log.
    /// </summary>
    private static IRenderable BuildTrackingDashboard(DiskWatcherService watcher, List<string> recentLog)
    {
        var header = new Markup(
            $"[bold]Monitoring...[/] [green]{watcher.FilesLoggedCount}[/] files logged to history, " +
            $"[grey]{watcher.ErrorsSuppressedCount}[/] access errors suppressed.");

        var hotTable = new Table().Border(TableBorder.Rounded).Title("[bold yellow]Hot Write Paths — last 5 min (manual cleanup only)[/]");
        hotTable.AddColumn("Folder");
        hotTable.AddColumn(new TableColumn("Writes/min").RightAligned());
        hotTable.AddColumn(new TableColumn("MB/min").RightAligned());
        hotTable.AddColumn("Alert");

        var hotFolders = watcher.GetHotFolders(5);
        if (hotFolders.Count == 0)
        {
            hotTable.AddRow("[grey]--[/]", "[grey]--[/]", "[grey]--[/]", "[grey]No notable activity yet[/]");
        }
        else
        {
            foreach (var folder in hotFolders)
            {
                string alert = folder switch
                {
                    { IsBurstAlert: true, IsChurnAlert: true } => "[red]🔴 CHURN + BURST[/]",
                    { IsBurstAlert: true } => "[red]🔴 BURST (big/fast file)[/]",
                    { IsChurnAlert: true } => "[yellow]🟡 CHURN (many small writes)[/]",
                    _ => "[grey]-[/]",
                };

                hotTable.AddRow(
                    Markup.Escape(folder.FolderPath),
                    folder.EventsPerMinute.ToString("F1", CultureInfo.InvariantCulture),
                    folder.MbPerMinute.ToString("F1", CultureInfo.InvariantCulture),
                    alert);
            }
        }

        var logLines = recentLog.Count == 0
            ? (IRenderable)new Markup("[grey]Waiting for activity...[/]")
            : new Markup(string.Join("\n", recentLog));

        var logPanel = new Panel(logLines).Header("[bold cyan] Recent Activity [/]").Expand();

        return new Rows(header, hotTable, logPanel);
    }
}
