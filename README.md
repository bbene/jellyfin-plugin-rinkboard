# Rink Board for Jellyfin 🏒

A Jellyfin plugin for [Rink Board](https://github.com/bbene), a hockey soundboard for the scorebox. Build the board in your browser from your Jellyfin music, and tablets running the Rink Board app download it, with all its audio, to play offline at the rink.

The plugin adds to Jellyfin:
- **A board editor** at `https://<your-jellyfin>/RinkBoard/`. Sign in with a Jellyfin administrator account; if you're already signed in to Jellyfin in that browser, it uses that session.
- **The board** itself: one shared board with game-situation pages, a ★ Main page of action buttons, playlists, and settings such as duck level and clip length.
- **Song hooks and levels.** Using Jellyfin's own FFmpeg, it finds where each song's loudest section kicks in, so "Auto clip" cues start on the hook, and measures its loudness, so the editor can show how much each song is adjusted when **Even out song levels** is on. (Tablets measure the songs themselves too, and play them at the same level.)
- **Uploads.** Clips like horns and short effects are uploaded from the editor into a folder of your music library, and Jellyfin scans them in.
- **Export and import.** A board and all its audio move as one zip, in the same format as the app's local mode. Import a tablet's local-mode export to move that setup into Jellyfin; its clips go into the upload folder and the cues are relinked to the new library songs. Songs that came from this Jellyfin aren't copied twice.
- **A small API for tablets** at `/RinkBoard/api/…`, which tablets reach with a Jellyfin API key.

Requires **Jellyfin 12** (12.0 or later). Version 1.0.0.0 is for Jellyfin 10.11; the catalog gives each server the right one.

## Install
1. **Dashboard → Plugins → Repositories → +**, and add
   `https://github.com/bbene/jellyfin-plugin-rinkboard/releases/latest/download/manifest.json`
2. Install **Rink Board** from the catalog and restart Jellyfin.
3. **Dashboard → Plugins → Rink Board**: tick the music libraries to search and set the upload folder.
4. **Dashboard → API Keys → +**: create a key for your tablets and enter it, with your Jellyfin address, in the app's Settings under **Jellyfin server**.

To install by hand, unzip a release's `rink-board_<version>.zip` into `<jellyfin config>/plugins/RinkBoard_<version>/` and restart Jellyfin.

If Jellyfin is behind Cloudflare Access or another login page, bypass `/RinkBoard/api/*` for the tablets. Cloudflare also limits uploads to 100 MB per request on its Free and Pro plans, so import big exports from your local network address instead of through the tunnel.

## Who can do what
Everything except the editor page itself needs a Jellyfin administrator: an admin signed in to the editor, or an API key (Jellyfin treats API keys as administrators). Other Jellyfin users can't read or change the board.

Uploads only ever write to the upload folder you set, and only files in that folder can be deleted from the editor. Library songs can't be.

## Build
Needs the .NET 10 SDK.

```bash
dotnet test Jellyfin.Plugin.RinkBoard.Tests   # board rules and hook detection
./package.sh                                  # builds dist/rink-board_<version>.zip and updates manifest.json
```

To publish a version: bump `<Version>` in `Jellyfin.Plugin.RinkBoard/Jellyfin.Plugin.RinkBoard.csproj`, run `./package.sh`, then create a GitHub release named `v<version>` with the zip and `manifest.json` attached. Jellyfin servers that added the repository see the update.

`Web/index.html` is the editor page. The Rink Board app bundles the same file for its local mode, so keep the two copies identical.

## License
GPL-3.0-or-later. See [LICENSE](LICENSE).
