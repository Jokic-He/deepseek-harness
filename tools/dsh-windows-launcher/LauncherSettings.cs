using System;
using System.IO;
using System.Text.Json;

namespace DshServiceLauncher;

/// <summary>
/// Launcher settings, persisted as JSON under %APPDATA%\DshServiceLauncher.
/// </summary>
internal sealed class LauncherSettings
{
    internal const string DefaultCommand = "pnpm dsh web";
    internal const string DefaultWebUiUrl = "http://127.0.0.1:3080";

    public string WorkingDirectory { get; set; } = string.Empty;
    public string Command { get; set; } = DefaultCommand;
    public string WebUiUrl { get; set; } = DefaultWebUiUrl;

    internal static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DshServiceLauncher");

    internal static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Load settings, falling back to defaults and repo-root detection.</summary>
    internal static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<LauncherSettings>(json);
                if (loaded is not null)
                    return Normalize(loaded);
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall through to defaults.
        }

        return new LauncherSettings { WorkingDirectory = DetectRepoRoot() };
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal: the UI still works with in-memory values.
        }
    }

    // The first build shipped a direct-node default that failed to start; if a
    // settings file still carries it, reset to the working pnpm default.
    private const string LegacyDirectNodeCommand = "node --import tsx/esm apps/cli/src/bin.ts web";

    private static LauncherSettings Normalize(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Command) || settings.Command == LegacyDirectNodeCommand)
            settings.Command = DefaultCommand;
        if (string.IsNullOrWhiteSpace(settings.WebUiUrl))
            settings.WebUiUrl = DefaultWebUiUrl;
        if (string.IsNullOrWhiteSpace(settings.WorkingDirectory))
            settings.WorkingDirectory = DetectRepoRoot();
        return settings;
    }

    /// <summary>
    /// Walk up from the executable looking for the dsh repo root: a package.json
    /// whose content declares "@deepseek-ai/dsh-root".
    /// </summary>
    internal static string DetectRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var manifest = Path.Combine(directory.FullName, "package.json");
            try
            {
                if (File.Exists(manifest)
                    && File.ReadAllText(manifest).Contains("\"@deepseek-ai/dsh-root\"", StringComparison.Ordinal))
                {
                    return directory.FullName;
                }
            }
            catch
            {
                // Unreadable manifest; keep walking up.
            }
            directory = directory.Parent;
        }
        return AppContext.BaseDirectory;
    }
}
