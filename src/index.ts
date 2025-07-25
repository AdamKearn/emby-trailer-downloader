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
    : item.Path}/trailers/Trailer - ${item.Name} (${item.ProductionYear})`;

  let downloaded = false;

  for (const url of trailerUrls) {
    try {
      const download = youtubeDl(url, {
        format: 'bestvideo[height<=1080]+bestaudio/best[height<=1080]',
        output: trailerPath,
        noCheckCertificates: true,
        noWarnings: true,
        addHeader: ['referer:youtube.com', 'user-agent:googlebot'],
        ...(env.PROXY_STRING ? { proxy: env.PROXY_STRING } : {}),
        ...(env.COOKIES_FILE ? { cookies: env.COOKIES_FILE } : {}),
      });

      await logger(download, `Downloading Trailer: [${item.Type === 'Movie' ? 'Movie' : 'TV'}] ${item.Name} (${item.ProductionYear})`);
      downloaded = true;
      break; // Exit loop after first successful download
    } catch (error: any) {
        console.error(`Error During Download: [${item.Type === 'Movie' ? 'Movie' : 'TV'}] ${item.Name} (${item.ProductionYear}):`, error.message.substring(0, error.message.indexOf('.')));
    }
  }

  if (!downloaded) {
    console.log(`All trailer downloads failed for ${item.Name}`);
  }
}
