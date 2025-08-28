# Emby Trailer Downloader
Download trailers locally to your media-server instead of streaming from youtube.

## Installation
First you need to download the repo and create an `.env` file to store the API key and URL to your Emby server.

```
git clone https://github.com/AdamKearn/emby-trailer-downloader.git
cd emby-trailer-downloader

sudo nano .env
```

Within the `.env` file it should look something like this.

```env
API_KEY_EMBY=XXXXXXXXXXXXXXXXX
EMBY_HOSTNAME=http://server:8096
PROXY_STRING=socks5://<username>:<password>@<location.server.com>:1080
COOKIES_FILE=/path/to/cookies.txt
```

## Dependencies
You will need to install the latest version of YT-DLP to allow the videos to download in the background.
You can use this website to get the install commands for your package manager.

https://github.com/yt-dlp/yt-dlp/wiki/Installation

If you are using linux I recommend using the APT option listed further down the page.

You will also needs to make sure you have NodeJS/NPM installed as well as Yarn.
I am currently using `NodeJS v18.16.0`, `NPM v9.6.6` and `Yarn v1.22.22`

```
npm install --global yarn
```

## Compiling
Now you have all the dependencies installed you should now be able to install the modules and create a build using the below commands.
After building you can now use the start command to run the script.  If you need to make modifications you can use `yarn run dev` for doing quick tests.

```
yarn install
yarn run build

yarn run start
```

This should now start to download all your trailers locally into a sub-folder within each movie/tv show.
Please note all movies will need to be within a folder and not on their own. e.g. `Movie Name (2025)\Moive.mp4`

## Automating Downloading.
Once you are happy how everything is running you can now setup a cronjob to run the script automatically.

The following cronjob will be run every day at 5am and downloads the latest version of yt-dlp from the [nightly build branch](https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest).

You can change this to fit your usage. Use this website for getting the formatting correctly: https://crontab.guru/

Make sure to change the path listed below to the correct location where you downloaded the repo.
```
crontab -e

0 5 * * * yarn --cwd /path/to/emby-trailer-downloader/ update-and-start
```
