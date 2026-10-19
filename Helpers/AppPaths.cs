namespace NativeTavern.Helpers;

public static class AppPaths
{
    private static string ResolveDefaultRoot() => Path.Combine(ResolveApplicationDirectory(), "UserData");

    public static string Root { get; private set; } = ResolveDefaultRoot();
    public static string DataDirectory => Path.Combine(Root, "Data");
    public static string LogsDirectory => Path.Combine(Root, "Logs");
    public static string CacheDirectory => Path.Combine(Root, "Cache");
    public static string AvatarsDirectory => Path.Combine(Root, "Avatars");
    public static string AttachmentsDirectory => Path.Combine(Root, "Attachments");
    public static string DocumentsDirectory => Path.Combine(Root, "Documents");
    public static string PluginsDirectory => Path.Combine(Root, "Plugins");
    public static string PluginDataDirectory => Path.Combine(Root, "PluginData");
    // Keep the existing library filename so upgrades reuse all saved conversations.
    public static string DatabaseFile => Path.Combine(DataDirectory, "NativeTavern.db");
    public static string LogFile => Path.Combine(LogsDirectory, "Lorehaven.log");

    // Lets non-desktop hosts (e.g. the MAUI Android app) redirect all managed data
    // to their own storage. Must be called before any path is read; the desktop
    // app never calls this and keeps the default layout.
    public static void UseRoot(string root) =>
        Root = string.IsNullOrWhiteSpace(root) ? ResolveDefaultRoot() : Path.GetFullPath(root);

    private static string ResolveApplicationDirectory()
    {
        var applicationDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var parent = Directory.GetParent(applicationDirectory)?.FullName;
        return string.Equals(Path.GetFileName(applicationDirectory), "publish", StringComparison.OrdinalIgnoreCase) &&
               parent is not null &&
               (File.Exists(Path.Combine(parent, "Lorehaven.csproj")) ||
                File.Exists(Path.Combine(parent, "NativeTavern.csproj")))
            ? parent
            : applicationDirectory;
    }

    public static void EnsureCreated()
    {
        MigrateLegacyData();
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(AvatarsDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
        Directory.CreateDirectory(DocumentsDirectory);
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(PluginDataDirectory);
    }

    private static void MigrateLegacyData()
    {
        if (File.Exists(DatabaseFile)) return;

        var source = GetLegacyRoots()
            .Where(path => File.Exists(Path.Combine(path, "Data", "NativeTavern.db")))
            .OrderByDescending(path =>
                Directory.Exists(Path.Combine(path, "Avatars")) &&
                Directory.EnumerateFiles(Path.Combine(path, "Avatars")).Any())
            .ThenByDescending(path =>
                File.GetLastWriteTimeUtc(Path.Combine(path, "Data", "NativeTavern.db")))
            .FirstOrDefault();
        if (source is null) return;

        CopyDirectory(source, Root);
    }

    private static IEnumerable<string> GetLegacyRoots()
    {
        var packagesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Local", "Packages");
        if (Directory.Exists(packagesRoot))
        {
            foreach (var package in Directory.EnumerateDirectories(packagesRoot, "OpenAI.Codex_*"))
            {
                yield return Path.Combine(
                    package, "LocalCache", "Local", "NativeTavern");
            }
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NativeTavern");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Overwrite so a previously interrupted migration cannot crash the next startup.
            File.Copy(file, target, true);
        }
    }
}
