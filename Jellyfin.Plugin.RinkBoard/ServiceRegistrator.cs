using System.IO;
using Jellyfin.Plugin.RinkBoard.App;
using Jellyfin.Plugin.RinkBoard.Audio;
using Jellyfin.Plugin.RinkBoard.Board;
using Jellyfin.Plugin.RinkBoard.Logs;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.RinkBoard;

/// <summary>Registers the board store, track analyzer, app builds and device logs, all kept in the plugin's data folder.</summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp => new BoardStore(DataFolder(sp)));
        serviceCollection.AddSingleton(sp => new TrackAnalyzer(DataFolder(sp)));
        serviceCollection.AddSingleton(sp => new AppReleases(DataFolder(sp)));
        serviceCollection.AddSingleton(sp => new DeviceLogs(DataFolder(sp)));
    }

    // Plugin.Instance isn't created yet when services are registered, so resolve the folder lazily.
    private static string DataFolder(System.IServiceProvider sp) =>
        Plugin.Instance?.DataFolderPath
        ?? Path.Combine(sp.GetRequiredService<IApplicationPaths>().DataPath, "rinkboard");
}
