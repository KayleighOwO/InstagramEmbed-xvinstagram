namespace InstagramEmbed.Application.Services
{
    /// <summary>
    /// Optional credentials for a single Instagram account the operator controls.
    /// Everything works with these left blank ("anonymous mode") — Instagram simply
    /// throttles/blocks anonymous requests more aggressively than logged-in ones.
    /// Setting SessionId (and ideally DsUserId) makes requests look like they come
    /// from that logged-in account, which is far more reliable, at the cost of using
    /// a real account for the fetching. This is still only talking to Instagram —
    /// no third-party downloader service is involved either way.
    ///
    /// To obtain these values: log into instagram.com in a browser with an account
    /// you're comfortable using for this, open dev tools → Application/Storage →
    /// Cookies → instagram.com, and copy the "sessionid" and "ds_user_id" values.
    /// </summary>
    public sealed class InstagramSettings
    {
        public string? SessionId { get; set; }
        public string? DsUserId { get; set; }
        public string? CsrfToken { get; set; }

        public bool HasSession => !string.IsNullOrWhiteSpace(SessionId);
    }
}
