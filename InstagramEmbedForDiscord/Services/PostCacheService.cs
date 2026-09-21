using InstagramEmbed.Application.Models;
using Microsoft.Extensions.Caching.Memory;

namespace InstagramEmbed.Application.Services;

/// <summary>
/// Fetches Instagram posts directly from Instagram via <see cref="InstagramMediaService"/>
/// and caches them in-process memory. No third-party downloader service involved.
/// </summary>
public sealed class PostCacheService
{
    private readonly IMemoryCache _cache;
    private readonly InstagramMediaService _instagram;
    private readonly ILogger<PostCacheService> _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(4);

    public PostCacheService(IMemoryCache cache, InstagramMediaService instagram, ILogger<PostCacheService> logger)
    {
        _cache = cache;
        _instagram = instagram;
        _logger = logger;
    }

    public async Task<CachedPost?> GetOrFetchAsync(string cacheId, string instagramUrl)
    {
        if (_cache.TryGetValue(cacheId, out CachedPost? cached))
            return cached;

        CachedPost? post;
        try
        {
            post = await _instagram.FetchAsync(cacheId, instagramUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch {Url} from Instagram", instagramUrl);
            return null;
        }

        if (post == null) return null;

        _cache.Set(cacheId, post, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheTtl,
            Size = 1
        });

        return post;
    }
}
