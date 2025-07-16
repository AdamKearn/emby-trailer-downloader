import { env } from "./env.js";
import type { EmbyResponse } from "./types.d.js";
import youtubeDl from 'youtube-dl-exec';
import createLogger from "progress-estimator";

const logger = createLogger({
  storagePath: ".progress-estimator",
});

const res = await fetch(
    `${env.EMBY_HOSTNAME}/emby/Items?HasTrailer=false&HasTmdbId=true&Recursive=true&Fields=RemoteTrailers,Path,ProductionYear&ExcludeItemTypes=Person,BoxSet&EnableImages=false&EnableUserData=false&api_key=${env.API_KEY_EMBY}`
  );

const data: EmbyResponse = await res.json();

for (const item of data.Items) {
  const trailerUrls = item.RemoteTrailers?.map(trailer => trailer.Url).filter(Boolean);
  if (!trailerUrls || trailerUrls.length === 0) continue;

  const trailerPath = `${item.Type === 'Movie'
    ? item.Path.substring(0, item.Path.lastIndexOf('/'))
    : item.Path}/trailers/Trailer - ${item.Name} (${item.ProductionYear}).mkv`;

  let downloaded = false;

  for (const url of trailerUrls) {
    try {
      const download = youtubeDl(url, {
        format: 'best',
        output: trailerPath,
        noCheckCertificates: true,
        noWarnings: true,
        addHeader: ['referer:youtube.com', 'user-agent:googlebot'],
      });

      await logger(download, `Downloading Trailer: ${item.Name} (${item.ProductionYear})`);
      downloaded = true;
      break; // Exit loop after first successful download
    } catch (error: any) {
      const isKnownError =
        error.message?.includes('unable to download video data') ||
        error.message?.includes('Private video') ||
        error.message?.includes('not made this video available in your country') ||
        error.message?.includes('Video unavailable') ||
        error.message?.includes('HTTP Error 403');

      if (isKnownError) {
        console.log(`Failed to download trailer from URL for ${item.Name} (${item.ProductionYear}): ${url}`);
      } else {
        console.error(`Unexpected error downloading trailer for ${item.Name} (${item.ProductionYear}):`, error);
      }
    }
  }

  if (!downloaded) {
    console.log(`All trailer downloads failed for ${item.Name}`);
  }
}
