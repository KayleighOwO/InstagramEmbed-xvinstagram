using System.Text.Json;
using System.Text.RegularExpressions;
using InstagramEmbed.Application.Models;
using Microsoft.Extensions.Options;

namespace InstagramEmbed.Application.Services
{
    /// <summary>
    /// Fetches Instagram post/reel/story media directly from Instagram's own
    /// servers. No snapsave.app, snaptik.app, or any other third-party
    /// downloader site is involved anywhere in this class — every request goes
    /// to instagram.com or i.instagram.com.
    ///
    /// Strategy, in order:
    ///   1. Instagram's own media-info endpoint, tried against both
    ///      i.instagram.com and www.instagram.com, with "guest" cookies primed
    ///      from a plain page load (the way a real browser would have them)
    ///      when no operator session is configured.
    ///   2. The public "/embed/captioned/" page, scraped as a best-effort
    ///      fallback.
    ///
    /// Current real-world behavior (observed in production logs): Instagram
    /// increasingly returns its logged-out web-app shell (HTML, "not-logged-in"
    /// class) instead of JSON for #1 when there's no valid session, and has
    /// also been locking down #2. In practice this means: without
    /// <see cref="InstagramSettings"/> configured, expect a low and declining
    /// success rate, not "works most of the time." Configuring a session from
    /// an account you control is not just an optimization anymore — it's
    /// close to required for reliable operation. It's still only Instagram
    /// you're talking to, just authenticated instead of anonymous.
    /// </summary>
    public sealed class InstagramMediaService
    {
        // Public, non-secret client id that instagram.com's own web frontend sends
        // on every browser request to its internal API. It identifies "the web
        // app" as the caller — it is not a login credential or a secret API key,
        // and it's the same value visible in any browser's network tab.
        private const string IgAppId = "936619743392459";

        // Also a public, static value sent by the web client on the same requests.
        private const string AsbdId = "129477";

        private static readonly string[] MediaInfoHosts = ["i.instagram.com", "www.instagram.com"];

        private const string Base64UrlAlphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

        private static readonly Regex PathRegex = new(
            @"instagram\.com/(?:(?<user>[^/]+)/)?(?<type>p|reel|reels|tv|share)/(?<code>[^/?#]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StoryRegex = new(
            @"instagram\.com/stories/(?<user>[^/]+)/(?<id>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex VideoUrlRegex = new("\"video_url\":\"(?<u>[^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex DisplayUrlRegex = new("\"display_url\":\"(?<u>[^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex CsrfCookieRegex = new(@"csrftoken=([^;]+)", RegexOptions.Compiled);

        private readonly HttpClient _http;
        private readonly ILogger<InstagramMediaService> _logger;
        private readonly InstagramSettings _settings;

        // Lazily-primed "guest" cookies (csrftoken/mid/ig_did/datr) from a plain
        // page load, reused across anonymous requests the way a real browser
        // would carry them. Only used when no operator session is configured.
        private readonly SemaphoreSlim _guestCookieLock = new(1, 1);
        private string? _guestCookieHeader;
        private DateTime _guestCookieExpiresUtc = DateTime.MinValue;

        public InstagramMediaService(IHttpClientFactory factory, ILogger<InstagramMediaService> logger,
            IOptions<InstagramSettings> settings)
        {
            _http = factory.CreateClient("instagram");
            _logger = logger;
            _settings = settings.Value;
        }

        public async Task<CachedPost?> FetchAsync(string cacheId, string instagramUrl, CancellationToken ct = default)
        {
            instagramUrl = await ResolveShareLinkAsync(instagramUrl, ct);

            var storyMatch = StoryRegex.Match(instagramUrl);
            if (storyMatch.Success)
            {
                if (!_settings.HasSession)
                {
                    _logger.LogInformation(
                        "Story requested but no Instagram session configured — stories generally require a logged-in session.");
                }
                return await FetchByMediaIdAsync(cacheId, instagramUrl, long.Parse(storyMatch.Groups["id"].Value), ct);
            }

            var match = PathRegex.Match(instagramUrl);
            if (!match.Success)
            {
                _logger.LogWarning("Could not parse Instagram URL {Url}", instagramUrl);
                return null;
            }

            string shortcode = match.Groups["code"].Value;

            long mediaId;
            try
            {
                mediaId = ShortcodeToMediaId(shortcode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not decode shortcode {Code}", shortcode);
                return null;
            }

            var post = await FetchByMediaIdAsync(cacheId, instagramUrl, mediaId, ct);
            if (post != null) return post;

            _logger.LogInformation("media-info API failed for {Code}, trying embed page fallback", shortcode);
            return await FetchFromEmbedPageAsync(cacheId, instagramUrl, shortcode, ct);
        }

        // ── Strategy 1: Instagram's own media-info API, tried against each host ──
        private async Task<CachedPost?> FetchByMediaIdAsync(string cacheId, string instagramUrl, long mediaId, CancellationToken ct)
        {
            foreach (var host in MediaInfoHosts)
            {
                var post = await TryMediaInfoHostAsync(cacheId, instagramUrl, mediaId, host, ct);
                if (post != null) return post;
            }
            return null;
        }

        private async Task<CachedPost?> TryMediaInfoHostAsync(string cacheId, string instagramUrl, long mediaId, string host, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://{host}/api/v1/media/{mediaId}/info/");

                req.Headers.Add("X-IG-App-ID", IgAppId);
                req.Headers.Add("X-ASBD-ID", AsbdId);
                req.Headers.Add("X-Requested-With", "XMLHttpRequest");
                req.Headers.Referrer = new Uri("https://www.instagram.com/");
                req.Headers.Add("Origin", "https://www.instagram.com");

                await ApplyCookiesAsync(req, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(12));

                using var resp = await _http.SendAsync(req, cts.Token);
                string body = await resp.Content.ReadAsStringAsync(cts.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    if (LooksLikeLoginWall(body))
                    {
                        _logger.LogWarning(
                            "{Host} returned {Status} (Instagram's logged-out web shell) for media {Id} — " +
                            "Instagram is gating this endpoint behind login right now. Configure " +
                            "Instagram:SessionId (see README) to fix this.",
                            host, resp.StatusCode, mediaId);
                    }
                    else
                    {
                        _logger.LogWarning("{Host} media-info API returned {Status} for media {Id}: {Body}",
                            host, resp.StatusCode, mediaId, Truncate(body));
                    }
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                {
                    _logger.LogWarning("{Host} media-info API returned no items for media {Id}", host, mediaId);
                    return null;
                }

                return BuildCachedPost(cacheId, instagramUrl, items[0]);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("{Host} media-info API timed out for media {Id}", host, mediaId);
                return null;
            }
            catch (JsonException)
            {
                _logger.LogWarning("{Host} media-info API returned non-JSON for media {Id}", host, mediaId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Host} media-info API failed for media {Id}", host, mediaId);
                return null;
            }
        }

        private static bool LooksLikeLoginWall(string body) =>
            body.Contains("not-logged-in", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("\"require_login\"", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("class=\"no-js", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Attaches a Cookie header: the operator's own session if configured,
        /// otherwise "guest" cookies primed from a plain page load. Also adds
        /// the matching X-CSRFToken header, which Instagram checks against the
        /// csrftoken cookie value.
        /// </summary>
        private async Task ApplyCookiesAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (_settings.HasSession)
            {
                var cookie = $"sessionid={_settings.SessionId}";
                if (!string.IsNullOrWhiteSpace(_settings.DsUserId)) cookie += $"; ds_user_id={_settings.DsUserId}";
                if (!string.IsNullOrWhiteSpace(_settings.CsrfToken))
                {
                    cookie += $"; csrftoken={_settings.CsrfToken}";
                    req.Headers.Add("X-CSRFToken", _settings.CsrfToken);
                }
                req.Headers.Add("Cookie", cookie);
                return;
            }

            var guestCookie = await GetGuestCookieHeaderAsync(ct);
            if (guestCookie == null) return;

            req.Headers.Add("Cookie", guestCookie);
            var csrfMatch = CsrfCookieRegex.Match(guestCookie);
            if (csrfMatch.Success)
                req.Headers.Add("X-CSRFToken", csrfMatch.Groups[1].Value);
        }

        /// <summary>
        /// Loads instagram.com once to pick up the baseline cookies (csrftoken,
        /// mid, ig_did, datr) a real browser would already be carrying, and
        /// caches them for a while. A from-nowhere request with zero cookies
        /// at all is an easy signal for Instagram's anti-bot checks to flag.
        /// </summary>
        private async Task<string?> GetGuestCookieHeaderAsync(CancellationToken ct)
        {
            if (_guestCookieHeader != null && DateTime.UtcNow < _guestCookieExpiresUtc)
                return _guestCookieHeader;

            await _guestCookieLock.WaitAsync(ct);
            try
            {
                if (_guestCookieHeader != null && DateTime.UtcNow < _guestCookieExpiresUtc)
                    return _guestCookieHeader;

                using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.instagram.com/");
                using var resp = await _http.SendAsync(req, ct);

                var cookies = new List<string>();
                if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var sc in setCookies)
                    {
                        var namePart = sc.Split(';')[0].Trim();
                        if (namePart.Contains('=')) cookies.Add(namePart);
                    }
                }

                _guestCookieHeader = cookies.Count > 0 ? string.Join("; ", cookies) : null;
                _guestCookieExpiresUtc = DateTime.UtcNow.AddMinutes(30);

                if (_guestCookieHeader == null)
                    _logger.LogWarning("Could not prime guest cookies from instagram.com (no Set-Cookie headers returned)");

                return _guestCookieHeader;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prime guest cookies from instagram.com");
                return null;
            }
            finally
            {
                _guestCookieLock.Release();
            }
        }

        private static CachedPost BuildCachedPost(string cacheId, string instagramUrl, JsonElement item)
        {
            var post = new CachedPost
            {
                ShortCode = cacheId,
                RawUrl = instagramUrl,
                AuthorUsername = GetString(item, "user", "username") ?? "NOT_SET",
                AuthorName = GetString(item, "user", "full_name"),
                AvatarUrl = GetString(item, "user", "profile_pic_url"),
                Caption = GetString(item, "caption", "text"),
                Likes = GetInt(item, "like_count"),
                Comments = GetInt(item, "comment_count"),
            };

            var mediaItems = new List<CachedMedia>();

            if (item.TryGetProperty("carousel_media", out var carousel) && carousel.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in carousel.EnumerateArray())
                {
                    var m = ExtractMedia(child);
                    if (m != null) mediaItems.Add(m);
                }
            }
            else
            {
                var m = ExtractMedia(item);
                if (m != null) mediaItems.Add(m);
            }

            post.Media.AddRange(mediaItems);

            if (mediaItems.Count > 0)
                post.DefaultThumbnailUrl = mediaItems[0].ThumbnailUrl;

            var (w, h) = GetDimensions(item);
            if (w > 0 && h > 0) { post.Width = w; post.Height = h; }

            return post;
        }

        private static CachedMedia? ExtractMedia(JsonElement item)
        {
            int mediaType = GetInt(item, "media_type"); // 1 = photo, 2 = video, 8 = carousel (handled by caller)

            string? thumb = null;
            if (item.TryGetProperty("image_versions2", out var iv2) &&
                iv2.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0)
            {
                thumb = candidates[0].GetProperty("url").GetString();
            }

            if (mediaType == 2 &&
                item.TryGetProperty("video_versions", out var videos) &&
                videos.ValueKind == JsonValueKind.Array &&
                videos.GetArrayLength() > 0)
            {
                var url = videos[0].GetProperty("url").GetString();
                if (string.IsNullOrWhiteSpace(url)) return null;
                return new CachedMedia { Url = url, MediaType = "video", ThumbnailUrl = thumb ?? url };
            }

            if (!string.IsNullOrWhiteSpace(thumb))
                return new CachedMedia { Url = thumb, MediaType = "image", ThumbnailUrl = thumb };

            return null;
        }

        private static (int width, int height) GetDimensions(JsonElement item)
        {
            if (item.TryGetProperty("original_width", out var w) && item.TryGetProperty("original_height", out var h) &&
                w.ValueKind == JsonValueKind.Number && h.ValueKind == JsonValueKind.Number &&
                w.TryGetInt32(out var wi) && h.TryGetInt32(out var hi))
            {
                return (wi, hi);
            }
            return (0, 0);
        }

        private static string? GetString(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static string? GetString(JsonElement el, string prop, string nested) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Object
                ? GetString(v, nested) : null;

        private static int GetInt(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out var i) ? i : 0;

        private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

        // ── Strategy 2: public embed page, best-effort fallback ─────────────
        private async Task<CachedPost?> FetchFromEmbedPageAsync(string cacheId, string instagramUrl, string shortcode, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://www.instagram.com/p/{shortcode}/embed/captioned/");
                req.Headers.Referrer = new Uri("https://www.instagram.com/");
                await ApplyCookiesAsync(req, ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                using var resp = await _http.SendAsync(req, cts.Token);
                string html = await resp.Content.ReadAsStringAsync(cts.Token);

                var videoMatch = VideoUrlRegex.Match(html);
                var displayMatch = DisplayUrlRegex.Match(html);

                if (!videoMatch.Success && !displayMatch.Success)
                {
                    _logger.LogWarning("embed page fallback found no media for {Code} (page likely blocked/changed)", shortcode);
                    return null;
                }

                string? mediaUrl = videoMatch.Success ? Unescape(videoMatch.Groups["u"].Value) : null;
                string? thumbUrl = displayMatch.Success ? Unescape(displayMatch.Groups["u"].Value) : mediaUrl;

                var post = new CachedPost { ShortCode = cacheId, RawUrl = instagramUrl };
                post.Media.Add(new CachedMedia
                {
                    Url = mediaUrl ?? thumbUrl!,
                    MediaType = mediaUrl != null ? "video" : "image",
                    ThumbnailUrl = thumbUrl ?? mediaUrl!
                });
                post.DefaultThumbnailUrl = thumbUrl;
                return post;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "embed page fallback failed for {Code}", shortcode);
                return null;
            }
        }

        private static string Unescape(string s) => s.Replace("\\u0026", "&").Replace("\\/", "/");

        // ── /share/... links resolve to a canonical /p/ or /reel/ URL via redirect ──
        private async Task<string> ResolveShareLinkAsync(string url, CancellationToken ct)
        {
            if (!url.Contains("/share/", StringComparison.OrdinalIgnoreCase)) return url;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
                {
                    return resp.Headers.Location.IsAbsoluteUri
                        ? resp.Headers.Location.ToString()
                        : new Uri(new Uri(url), resp.Headers.Location).ToString();
                }

                // Handler follows redirects automatically in most configurations,
                // so the final URL usually shows up here instead.
                return resp.RequestMessage?.RequestUri?.ToString() ?? url;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve share link {Url}", url);
                return url;
            }
        }

        // ── shortcode → numeric media id, Instagram's own base64-style alphabet ──
        public static long ShortcodeToMediaId(string shortcode)
        {
            long id = 0;
            foreach (char c in shortcode)
            {
                int idx = Base64UrlAlphabet.IndexOf(c);
                if (idx < 0) throw new ArgumentException($"Invalid shortcode character '{c}'");
                id = id * 64 + idx;
            }
            return id;
        }
    }
}
