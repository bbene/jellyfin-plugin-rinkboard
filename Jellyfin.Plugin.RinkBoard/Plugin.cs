using System;
using System.Collections.Generic;
using Jellyfin.Plugin.RinkBoard.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RinkBoard;

/// <summary>
/// Rink Board: hosts a hockey soundboard's board, editor and audio inside Jellyfin,
/// so the Rink Board app needs no separate server.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Rink Board";

    public override Guid Id => Guid.Parse("7b311deb-536a-415d-baa6-00abdaa6e566");

    public override string Description => "Build a hockey soundboard in the browser and play it offline on tablets with the Rink Board app.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "RinkBoard",
            DisplayName = "Rink Board",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        },
    ];
}
