using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sentinel.Core.Updates;

namespace Sentinel.App.Updates;

/// <summary>Where this copy of Sentinel runs from, and whether it can replace itself.</summary>
public static class UpdateEnvironment
{
    public static string InstallDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static string UpdatesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sentinel", "updates");

    public static AppVersion CurrentVersion => AppVersion.FromAssembly(typeof(UpdateEnvironment).Assembly.GetName().Version);

    public static string Runtime => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>Developer builds (run from bin\) and read-only locations can check for updates but never replace themselves.</summary>
    public static bool CanSelfUpdate(out string reason)
    {
        var dir = InstallDirectory + Path.DirectorySeparatorChar;
        if (dir.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) || dir.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
        {
            reason = "This is a development build, so it is not updated automatically.";
            return false;
        }
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "Sentinel's folder is read-only, so updates must be installed manually.";
            return false;
        }
        reason = "";
        return true;
    }

    public static string ResultFile => Path.Combine(UpdatesRoot, "last-install.json");
}

public sealed record InstallResult(string From, string To, bool Succeeded, string Message, DateTimeOffset Time);

/// <summary>
/// Runs from the <em>new</em> version's staged folder (<c>Sentinel.exe --install-update</c>). Waits for the old
/// instance to exit, moves the old installation aside, copies the new files in, and relaunches. Any failure restores
/// the previous installation untouched. Only Sentinel's own folder is modified; no elevation is ever requested.
/// </summary>
public static class UpdateInstaller
{
    public static int Run(string[] args)
    {
        var target = Arg(args, "--target");
        var pid = int.TryParse(Arg(args, "--pid"), out var p) ? p : 0;
        var background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var source = UpdateEnvironment.InstallDirectory;
        var to = UpdateEnvironment.CurrentVersion.ToString();
        Log($"Installing {to} from {source} into {target}");

        if (string.IsNullOrEmpty(target) || !File.Exists(Path.Combine(target, "Sentinel.exe")) ||
            string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            Log("Refusing: target is not an existing Sentinel installation");
            return 2;
        }
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var from = FileVersionInfo.GetVersionInfo(Path.Combine(target, "Sentinel.exe")).ProductVersion?.Split('+')[0] ?? "unknown";

        WaitForExit(pid);
        var backup = target + ".previous";
        try
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            Retry(() => Directory.Move(target, backup));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Record(from, to, false, "Sentinel's folder was in use, so the update will be retried next time.");
            Log("Could not move the current installation aside: " + ex.Message);
            Launch(target, background, null);
            return 3;
        }

        try
        {
            CopyDirectory(source, target);
            Record(from, to, true, $"Updated from {from} to {to}.");
            Log("Installed successfully");
            Launch(target, background, "--updated");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("Copy failed, restoring previous version: " + ex.Message);
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Retry(() => Directory.Move(backup, target));
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
            {
                Log("Restore failed: " + restore.Message);
            }
            Record(from, to, false, "The update could not be installed, so the previous version was kept.");
            Launch(target, background, null);
            return 4;
        }
    }

    private static void WaitForExit(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.WaitForExit(TimeSpan.FromSeconds(60))) Log("Previous instance did not exit within 60 s");
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
        // File handles (including memory-mapped DLLs) can outlive the process object briefly.
        Thread.Sleep(500);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private static void Retry(Action action)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void Launch(string dir, bool background, string? flag)
    {
        var psi = new ProcessStartInfo(Path.Combine(dir, "Sentinel.exe")) { UseShellExecute = false, WorkingDirectory = dir };
        psi.ArgumentList.Add("--after-restart");
        if (flag is not null) psi.ArgumentList.Add(flag);
        if (background) psi.ArgumentList.Add("--background");
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log("Relaunch failed: " + ex.Message);
        }
    }

    private static void Record(string from, string to, bool ok, string message)
    {
        try
        {
            Directory.CreateDirectory(UpdateEnvironment.UpdatesRoot);
            File.WriteAllText(UpdateEnvironment.ResultFile, JsonSerializer.Serialize(new InstallResult(from, to, ok, message, DateTimeOffset.Now)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(UpdateEnvironment.UpdatesRoot);
            File.AppendAllText(Path.Combine(UpdateEnvironment.UpdatesRoot, "install.log"), $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Starts the staged version's installer for this installation. The caller must exit straight away.</summary>
    public static bool HandOff(StagedUpdate staged, bool background)
    {
        var psi = new ProcessStartInfo(Path.Combine(staged.AppDirectory, "Sentinel.exe")) { UseShellExecute = false, WorkingDirectory = staged.AppDirectory };
        psi.ArgumentList.Add("--install-update");
        psi.ArgumentList.Add("--target");
        psi.ArgumentList.Add(UpdateEnvironment.InstallDirectory);
        psi.ArgumentList.Add("--pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (background) psi.ArgumentList.Add("--background");
        try
        {
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
