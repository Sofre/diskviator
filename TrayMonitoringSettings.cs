using System.Text.Json;

namespace DiskViator;

internal static class TrayMonitoringSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskViator",
        "TrayMonitoring.json");

    public static IReadOnlyList<string> Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return [@"C:\"];
        }

        var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(SettingsPath))
            ?? throw new InvalidDataException($"Tray monitoring settings are empty: {SettingsPath}");
        return ValidatePaths(paths);
    }

    public static void Save(IEnumerable<string> paths)
    {
        string[] validatedPaths = ValidatePaths(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(validatedPaths, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string[] ValidatePaths(IEnumerable<string> paths)
    {
        var validatedPaths = new List<string>();
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"Tray monitoring folder does not exist: {path}");
            }

            string fullPath = Path.GetFullPath(path);
            if (validatedPaths.Any(existingPath => PathsOverlap(existingPath, fullPath)))
            {
                throw new ArgumentException($"Tray monitoring folders cannot overlap: {fullPath}");
            }

            validatedPaths.Add(fullPath);
        }

        if (validatedPaths.Count == 0)
        {
            throw new ArgumentException("At least one tray monitoring folder is required.");
        }

        return validatedPaths.ToArray();
    }

    private static bool PathsOverlap(string firstPath, string secondPath) =>
        IsSameOrDescendant(firstPath, secondPath) || IsSameOrDescendant(secondPath, firstPath);

    private static bool IsSameOrDescendant(string path, string parentPath)
    {
        if (string.Equals(path, parentPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string relativePath = Path.GetRelativePath(parentPath, path);
        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
