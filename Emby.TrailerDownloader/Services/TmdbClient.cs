using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.TrailerDownloader.Services
{
    /// <summary>
    /// Optional extra trailer source. Emby only stores a few trailer URLs per item (often one language);
    /// TMDB usually lists several trailers/teasers in multiple languages, which gives more chances to find a
    /// video that is not region-blocked.
    /// </summary>
    public class TmdbClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;

        public TmdbClient(IJsonSerializer json, ILogger logger)
        {
            _json = json;
            _logger = logger;
        }

        public async Task<IReadOnlyList<string>> GetTrailerUrlsAsync(
            string tmdbId, bool isSeries, string originalLanguage, CancellationToken cancellationToken)
        {
            var apiKey = Plugin.Instance.Configuration.TmdbApiKey?.Trim();
            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(tmdbId)) return Array.Empty<string>();

            var preferred = NormaliseLanguage(Plugin.Instance.Configuration.PreferredLanguage) ?? "en";
            var languages = new[] { preferred, "en", NormaliseLanguage(originalLanguage), "null" }
                .Where(l => !string.IsNullOrEmpty(l))
                .Distinct()
                .ToArray();

            var url = $"https://api.themoviedb.org/3/{(isSeries ? "tv" : "movie")}/{Uri.EscapeDataString(tmdbId)}/videos" +
                      $"?include_video_language={string.Join(",", languages)}";

            var isBearer = apiKey.StartsWith("eyJ", StringComparison.Ordinal);
            if (!isBearer) url += "&api_key=" + Uri.EscapeDataString(apiKey);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (isBearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    using (var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.Warn("TMDB returned {0} for {1} {2}", (int)response.StatusCode, isSeries ? "tv" : "movie", tmdbId);
                            return Array.Empty<string>();
                        }

                        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var parsed = _json.DeserializeFromString<TmdbVideosResponse>(body);
                        return Rank(parsed?.results, preferred);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.ErrorException("TMDB video lookup failed for {0}", ex, tmdbId);
                return Array.Empty<string>();
            }
        }

        private static IReadOnlyList<string> Rank(List<TmdbVideo> videos, string preferredLanguage)
        {
            if (videos == null) return Array.Empty<string>();

            return videos
                .Where(v => !string.IsNullOrEmpty(v.key))
                .Where(v => string.Equals(v.site, "YouTube", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(v.site, "Vimeo", StringComparison.OrdinalIgnoreCase))
                .Where(v => string.Equals(v.type, "Trailer", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(v.type, "Teaser", StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => string.Equals(v.type, "Trailer", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => string.Equals(v.iso_639_1, preferredLanguage, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => v.official ? 0 : 1)
                .ThenByDescending(v => v.size)
                .Select(v => string.Equals(v.site, "Vimeo", StringComparison.OrdinalIgnoreCase)
                    ? "https://vimeo.com/" + v.key
                    : "https://www.youtube.com/watch?v=" + v.key)
                .Distinct()
                .ToList();
        }

        private static string NormaliseLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return null;
            language = language.Trim();
            return language.Length > 2 ? language.Substring(0, 2).ToLowerInvariant() : language.ToLowerInvariant();
        }

        // ReSharper disable InconsistentNaming - mirrors TMDB's JSON
        public class TmdbVideosResponse
        {
            public List<TmdbVideo> results { get; set; }
        }

        public class TmdbVideo
        {
            public string iso_639_1 { get; set; }
            public string key { get; set; }
            public string site { get; set; }
            public string type { get; set; }
            public bool official { get; set; }
            public int size { get; set; }
        }
    }
}
