namespace Sefirah.Utils;

/// <summary>
/// Locates scrcpy/adb and prepares their process environment.
/// GUI apps on macOS start with a minimal PATH (/usr/bin:/bin:/usr/sbin:/sbin), so
/// Homebrew installs are invisible unless their bin directories are searched explicitly.
/// Search order: app bundle, PATH, then package manager bin directories.
/// </summary>
public static class ExternalToolLocator
{
    public const string Scrcpy = "scrcpy";
    public const string Adb = "adb";

    private static readonly string[] MacPackageManagerBinDirectories =
    [
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/opt/local/bin"
    ];

    public static string ExecutableName(string tool) => OperatingSystem.IsWindows() ? $"{tool}.exe" : tool;

    /// <summary>
    /// The macOS bundle ships the official scrcpy release (scrcpy, adb, scrcpy-server, icons) in
    /// Contents/Resources/scrcpy, next to the .NET runtime in Contents/Resources/runtime.
    /// scrcpy finds its server and icons beside its own executable, so no extra variables are needed.
    /// </summary>
    public static string? BundledDirectory
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return null;
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "scrcpy"));
            return Directory.Exists(directory) ? directory : null;
        }
    }

    /// <summary>
    /// Fills an empty or stale scrcpy/adb setting with an executable found on this machine.
    /// </summary>
    public static void EnsureConfigured(IGeneralSettingsService settings)
    {
        if (!File.Exists(settings.ScrcpyPath) && Locate(Scrcpy) is string scrcpyPath)
        {
            settings.ScrcpyPath = scrcpyPath;
        }

        if (!File.Exists(settings.AdbPath))
        {
            var companion = CompanionPath(settings.ScrcpyPath, Adb);
            if ((companion ?? Locate(Adb)) is string adbPath)
            {
                settings.AdbPath = adbPath;
            }
        }
    }

    public static string? Locate(string tool)
    {
        var name = ExecutableName(tool);
        return SearchDirectories()
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Returns the sibling tool next to <paramref name="selectedPath"/>, if present.
    /// </summary>
    public static string? CompanionPath(string? selectedPath, string tool)
    {
        if (string.IsNullOrEmpty(selectedPath)) return null;

        var directory = Path.GetDirectoryName(selectedPath);
        if (string.IsNullOrEmpty(directory)) return null;

        var companionPath = Path.GetFullPath(Path.Combine(directory, ExecutableName(tool)));
        return File.Exists(companionPath) ? companionPath : null;
    }

    /// <summary>
    /// The native macOS picker resolves symlinks, turning /opt/homebrew/bin/scrcpy into a
    /// versioned Cellar path that disappears on the next brew upgrade. Prefer the stable link.
    /// </summary>
    public static string NormalizePickedPath(string path)
    {
        if (!OperatingSystem.IsMacOS()) return path;
        if (!path.Contains("/Cellar/", StringComparison.Ordinal) && !path.Contains("/Caskroom/", StringComparison.Ordinal))
            return path;

        var name = Path.GetFileName(path);
        var stablePath = MacPackageManagerBinDirectories
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
        return stablePath ?? path;
    }

    /// <summary>
    /// scrcpy launches adb itself; point it at the configured adb and make package manager
    /// directories reachable for any other helpers it spawns.
    /// </summary>
    public static void ConfigureEnvironment(ProcessStartInfo startInfo, string? adbPath)
    {
        if (File.Exists(adbPath))
        {
            startInfo.Environment["ADB"] = adbPath;
        }

        if (OperatingSystem.IsWindows()) return;

        var existing = startInfo.Environment.TryGetValue("PATH", out var path) ? path : null;
        var entries = (existing ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var additions = new List<string>();
        if (Path.GetDirectoryName(adbPath) is { Length: > 0 } adbDirectory)
            additions.Add(adbDirectory);
        if (OperatingSystem.IsMacOS())
            additions.AddRange(MacPackageManagerBinDirectories);

        foreach (var directory in additions.Where(Directory.Exists).Reverse())
        {
            if (!entries.Contains(directory))
                entries.Insert(0, directory);
        }
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, entries);
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        // Prefer the bundled, version-matched pair so scrcpy and adb never disagree.
        IEnumerable<string> bundled = BundledDirectory is string bundledDirectory ? [bundledDirectory] : [];
        var extra = OperatingSystem.IsMacOS() ? MacPackageManagerBinDirectories : [];
        return bundled.Concat(pathEntries).Concat(extra).Distinct();
    }
}
