# InstagramEmbed (vxinstagram)

**InstagramEmbed** is a lightweight Instagram link embedding tool designed for Discord and other platforms supporting the [Open Graph Protocol (OGP)](https://ogp.me/). It offers full support for embedding Instagram photos and videos, enabling seamless previews across chats, forums, and websites.

InstagramEmbed talks **directly to Instagram** — there is no snapsave.app, snaptik.app, or any other third-party downloader site in the request path. It calls Instagram's own media-info endpoint (the same one instagram.com's own clients use), with an optional operator-owned Instagram session for reliability, and falls back to Instagram's public embed page if that fails.

## Features

- Embed Instagram **photos** and **videos** anywhere OGP is supported
- Fetches media straight from Instagram — no third-party API in the middle
- Fast response

## A note on reliability

Instagram actively rate-limits and blocks anonymous scraping — this affects every self-hosted Instagram embed tool, not just this one (see e.g. [Wikidepia/InstaFix](https://github.com/Wikidepia/InstaFix), archived after Instagram started blocking its embed-page scraping). Running this with no session configured will work for some posts and fail for others.

For consistent results, configure `Instagram:SessionId` (and ideally `Instagram:DsUserId`) with a session from an Instagram account you control — see [Configuration](#configuration) below. This still only talks to Instagram; it just authenticates as an account you own instead of going in anonymously.

## Supports

- ✅ Posts (www.instagram.com/p).
- ✅ Reels (www.instagram.com/reel(s)).
- ✅ Stories (www.instagram.com/stories/username).
- ✅ Albums (www.instagram.com/p/).
- ✅ User Posts (www.instagram.com/username/p).
- ✅ Share (www.instagram.com/share/p).
- ✅ Singular Media in Album (www.instagram.com/p/[hash]/1).
- ✅ With or Without Post Details (vxinstagram.com / d.vxinstagram.com) — restored: caption, author, and like/comment counts come back whenever the primary Instagram API call succeeds (most reliable with a configured session; not populated when the embed-page fallback is what ends up serving the request).
- ❌ User information, list of reels, list of posts, tags, and search queries.

For more information, visit [vxinstagram.com](https://vxinstagram.com)

## Deployment

### Requirements
- Docker
- A domain pointing to your server
- A reverse proxy (Nginx, Caddy, etc.) for TLS

### Run from Docker Hub

Pull and run the pre-built image:

```bash
docker pull alsauce/vxinstagram:latest

docker run -d \
  --name vxinstagram \
  --restart unless-stopped \
  -p 8080:8080 \
  alsauce/vxinstagram:latest
```

Point your reverse proxy at port `8080`.

### Build from Source

Clone the repository and build the Docker image:

```bash
git clone https://github.com/Lainmode/InstagramEmbed-vxinstagram
cd InstagramEmbed-vxinstagram/InstagramEmbedForDiscord
docker build -t vxinstagram .
docker run -d \
  --name vxinstagram \
  --restart unless-stopped \
  -p 8080:8080 \
  vxinstagram
```


## How It Works

1. User pastes an Instagram link with "vx" at the beginning (vxinstagram) (e.g., into Discord).
2. The page generates a preview with embedded media using Open Graph tags.
3. The platform (e.g., Discord) renders the embed automatically.

## Configuration

All settings can be provided via `appsettings.json`, environment variables (`Section__Key` form), or `dotnet user-secrets`.

| Setting | Env var | Required | Purpose |
|---|---|---|---|
| `Instagram:SessionId` | `Instagram__SessionId` | No | Instagram `sessionid` cookie value from an account you control. Enables authenticated fetching. |
| `Instagram:DsUserId` | `Instagram__DsUserId` | No | Matching `ds_user_id` cookie value. Recommended alongside `SessionId`. |
| `Instagram:CsrfToken` | `Instagram__CsrfToken` | No | Matching `csrftoken` cookie value. Optional extra reliability. |

**To obtain these values:** log into instagram.com in a browser using an account you're comfortable using for this purpose, open dev tools → Application (or Storage) → Cookies → `instagram.com`, and copy the `sessionid` and `ds_user_id` values. Treat them like a password — anyone with them can act as that account.

Leaving these blank runs the app in fully anonymous mode. It'll still work for a meaningful share of requests, just less consistently — see [A note on reliability](#a-note-on-reliability).

## Credits

- Backend by [Lainmode](https://github.com/Lainmode)
- Twitter: [@realAlita](https://twitter.com/realAlita)

## Support

If you like this project, consider [buying me a coffee](https://www.buymeacoffee.com/alsauce)!

## License

MIT License. See [`LICENSE`](./LICENSE) for details.
