using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace XPilot.Desktop;

/// <summary>
/// Keeps an installed copy of the game up to date. When the game was installed with the installer, it checks
/// the GitHub releases in the background, downloads a newer version, and hands it to Velopack to install once
/// the game quits, so nobody is pulled out of a match. Copies run from a zip or from source do nothing.
/// </summary>
public sealed class GameUpdater
{
    public const string RepositoryUrl = "https://github.com/yngveerlandsen/XPilot";

    private volatile string? _status;

    private GameUpdater()
    {
    }

    /// <summary>A line for the main menu, e.g. that an update is ready; null when there is nothing to say.</summary>
    public string? Status => _status;

    /// <summary>This build's version, e.g. "0.4.0", or null for a development build.</summary>
    public static string? Version
    {
        get
        {
            var version = typeof(GameUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // .NET appends "+<commit>" to the informational version; players only need the number.
            version = version?.Split('+')[0];
            return version is null or "1.0.0" ? null : version;
        }
    }

    /// <summary>Starts checking for updates in the background if this copy was installed.</summary>
    /// <remarks>XPILOT_UPDATE_SOURCE (a folder or URL with a Velopack feed) overrides GitHub, for testing.</remarks>
    public static GameUpdater Start()
    {
        var updater = new GameUpdater();
        try
        {
            var source = Environment.GetEnvironmentVariable("XPILOT_UPDATE_SOURCE");
            var manager = string.IsNullOrWhiteSpace(source)
                ? new UpdateManager(new GithubSource(RepositoryUrl, null, false))
                : new UpdateManager(source);
            if (manager.IsInstalled) _ = Task.Run(() => updater.CheckAsync(manager));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Update check not started: {ex.Message}");
        }
        return updater;
    }

    private async Task CheckAsync(UpdateManager manager)
    {
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (update == null) return;
            var version = update.TargetFullRelease.Version;
            _status = $"DOWNLOADING UPDATE {version}...";
            await manager.DownloadUpdatesAsync(update);
            // Installs the update after the game exits, without starting it again.
            manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
            _status = $"UPDATE {version} READY - IT INSTALLS WHEN YOU QUIT";
        }
        catch (Exception ex)
        {
            // Offline, GitHub unreachable, rate limited: try again next time the game starts.
            _status = null;
            Console.Error.WriteLine($"Update check failed: {ex.Message}");
        }
    }
}
