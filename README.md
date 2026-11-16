# Emby Trailer Downloader (plugin)

An Emby Server plugin that downloads trailers for your movies and TV shows and saves them locally next to the media, so they play without streaming from YouTube.

> v3 replaces the old Node.js script. Everything now runs inside Emby: no cron job, `.env` file, Node or manual yt-dlp install.

## How it works

- **New items:** when a movie or series is added to the library, it is queued for a trailer. When a **season or episode** is added, its **series** is queued instead. Duplicates are merged and series that already have a trailer are skipped, so a show only gets one trailer however many episodes arrive.
- **Waits for metadata:** the plugin waits a couple of minutes, re-checking up to three times, so Emby can fetch the item's trailer links first.
- **Where trailers go:**
  - `Movie (2024)/trailers/Movie (2024) - Trailer.mp4`
  - `Show/trailers/Show (2020) - Trailer.mp4`
  - If several movies share one folder, it uses `<movie file>-trailer.mp4` next to the movie instead.

  Emby picks them up as local trailers automatically. The plugin refreshes the item so this happens straight away.
- **Getting round blocked videos:** the plugin tries every source in this order until one works:
  1. The trailer links Emby already has for the item.
  2. Extra trailers and teasers from TMDB, in your preferred language, English and the original language. Needs an optional TMDB API key.
  3. A YouTube search for `<title> <year> official trailer`. It only accepts results under 10 minutes with "trailer" or "teaser" in the title.

  Each link is tried directly first, then through your proxy if you have set one.
- **Retries:** an item that still fails is retried later, waiting longer each time (6h, 12h, 24h … up to 7 days). It is given up on after a set number of attempts (default 10).
- **Self-managed tools:** the plugin downloads [yt-dlp](https://github.com/yt-dlp/yt-dlp) (nightly by default) and [Deno](https://deno.com) into its own data folder and updates them daily. Deno is the JavaScript runtime yt-dlp now needs for YouTube. The plugin uses Emby's bundled ffmpeg to merge the best video and audio streams.

## Re-downloading a single trailer

Use the **"..." menu → Refresh metadata** on any movie or TV show:

| What you pick in the refresh dialog | What happens |
| --- | --- |
| **Search for missing metadata** (no trailer yet) | Downloads the trailer about 20 seconds later. Skips the retry wait and the give-up limit. |
| **Replace all metadata** ticked | Downloads the trailer again and replaces the one the plugin saved before. Trailers you added yourself (other file names) are left alone. |

Emby has no way for plugins to add their own "..." menu entries, so the plugin hooks into the existing Refresh metadata option instead. Library scans and other automatic refreshes don't trigger this.

## Scheduled tasks

These appear under **Dashboard → Scheduled Tasks → Trailer Downloader**:

| Task | Default | Purpose |
| --- | --- | --- |
| Download missing trailers | Daily, 03:00 | Fills in trailers for your existing library and anything that was missed. **Run it once by hand after installing.** |
| Retry all failed trailers now | Manual | Clears the retry history and tries every missing trailer again. Use it after adding a proxy or cookies. |
| Update yt-dlp | Daily, 02:00 | Keeps yt-dlp and Deno up to date. |

## Installation

1. Download `Emby.TrailerDownloader.dll` from the latest [release](https://github.com/AdamKearn/emby-trailer-downloader/releases/latest).
   Or build it yourself with `dotnet build Emby.TrailerDownloader -c Release`. The output is `Emby.TrailerDownloader/bin/Release/netstandard2.0/Emby.TrailerDownloader.dll`.
2. Copy the DLL into Emby's `plugins` folder:
   - Linux: `/var/lib/emby/plugins/`
   - Docker: `/config/plugins/`
   - Windows: `%AppData%\Emby-Server\programdata\plugins\`
3. Restart Emby.
4. Open **Dashboard → Plugins → Trailer Downloader** to configure it.
5. Run **Download missing trailers** once.

Requires **Emby Server 4.9+**.

## Settings

| Setting | Notes |
| --- | --- |
| Download when new items are added | The automatic trigger. Turn it off to rely only on the scheduled task. |
| Movies / TV series | Which kinds of item get trailers. |
| Delay after item is added | Default 2 minutes. |
| TMDB API key | Optional, but recommended: it gives more alternative links when a video is region-blocked. Get a free key at <https://www.themoviedb.org/settings/api>. A v3 key or a v4 read token both work. |
| Preferred trailer language | 2-letter code, e.g. `en`. |
| Search YouTube if every known link fails | On by default. |
| Maximum resolution | Default 1080p. |
| Proxy | e.g. `socks5://user:pass@host:1080`. Use it with **Only when a direct download fails** (default) or **Always**. |
| Cookies file | Netscape-format `cookies.txt` exported from a browser signed in to YouTube. Fixes "Sign in to confirm you're not a bot" and age-restricted trailers. |
| yt-dlp / ffmpeg path | Overrides for platforms with no prebuilt yt-dlp (e.g. FreeBSD/TrueNAS CORE), or if you want your own builds. |
| Extra yt-dlp arguments | Added to every yt-dlp call. |

## Troubleshooting

- **Logs:** look in the Emby server log for lines from `TrailerDownloader`.
- **Region-blocked trailers:** add a TMDB key (more alternative trailers) and a proxy in an unblocked country.
- **"Sign in to confirm you're not a bot":** add a cookies file. A proxy that isn't a datacenter IP also helps.
- **Failure history:** stored in `plugins/configurations/TrailerDownloader/failures.json`. Delete it, or run **Retry all failed trailers now**, to start over.

## Development

```
dotnet build Emby.TrailerDownloader -c Release
```

Every push and pull request is built by GitHub Actions, and the DLL is uploaded as a build artifact.

### Publishing a release

Push a version tag:

```
git tag v3.1.0
git push origin v3.1.0
```

The **Release plugin** workflow then:
- builds the DLL with that version number, which is the version Emby shows;
- creates a GitHub Release with auto-generated notes;
- attaches `Emby.TrailerDownloader.dll` and a `.sha256` checksum.

Tags with a suffix, such as `v3.1.0-beta.1`, are published as pre-releases.

The project targets `netstandard2.0` and builds against the `MediaBrowser.Server.Core` NuGet package. The code is organised like this:

| File | Role |
| --- | --- |
| `TrailerEntryPoint.cs` | Hooks into `ILibraryManager.ItemAdded` and runs the delayed queue. |
| `TrailerRefreshProvider.cs` | Queues a download (or forced re-download) when a user runs Refresh metadata. |
| `Services/TrailerService.cs` | Picks the item to process, detects existing trailers, chooses download URLs, runs yt-dlp and moves the file into place. |
| `Services/ToolManager.cs` | Installs and updates yt-dlp and Deno. |
| `Services/TmdbClient.cs` | Optional TMDB lookups. |
| `Services/FailureStore.cs` | Retry and backoff history. |
| `ScheduledTasks/` | The three scheduled tasks. |
| `Configuration/` | Plugin settings and the dashboard page. |
