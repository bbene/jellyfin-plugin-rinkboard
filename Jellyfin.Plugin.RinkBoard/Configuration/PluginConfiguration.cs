using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RinkBoard.Configuration;

/// <summary>
/// Settings shown on the plugin's dashboard page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the music libraries the editor searches (library item ids). Empty means all.
    /// </summary>
#pragma warning disable CA1819 // XML-serialized settings need a settable array
    public string[] LibraryIds { get; set; } = [];
#pragma warning restore CA1819

    /// <summary>
    /// Gets or sets the folder that clips uploaded in the editor are saved to. It should be a
    /// folder of one of the selected music libraries so Jellyfin picks the files up. Empty turns
    /// uploads off.
    /// </summary>
    public string UploadFolder { get; set; } = string.Empty;
}
