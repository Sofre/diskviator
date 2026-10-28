using System.Diagnostics;

namespace DiskViator;

/// <summary>
/// Registers or removes the Task Scheduler job that starts background tracking (tray mode)
/// at logon with highest privileges. Creating the job requires an elevated (Administrator) shell.
/// </summary>
internal static class StartupTask
{
    public const string TaskName = "DiskViator Background Tracking";

    public static int Install()
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the executable path.");

        string command = $"\"{exePath}\"";

        // Under `dotnet run` / dotnet-tool hosts the process is dotnet.exe, so the app DLL must be passed too.
        if (Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            command += $" \"{typeof(StartupTask).Assembly.Location}\"";
        }

        command += " --tray";

        return RunSchtasks(
            ["/Create", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F", "/TR", command],
            "Background tracking will start at logon with admin rights.");
    }

    public static int Remove() =>
        RunSchtasks(["/Delete", "/TN", TaskName, "/F"], "Background tracking startup task removed.");

    private static int RunSchtasks(string[] arguments, string successMessage)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start schtasks.exe.");

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0)
        {
            Console.WriteLine(successMessage);
            return 0;
        }

        Console.WriteLine(output.Trim());
        if (output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Run this command again from an elevated terminal (Run as administrator).");
        }

        return process.ExitCode;
    }
}
