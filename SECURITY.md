# Security

## Reporting a problem

Please don't open a public issue for a security problem. Use GitHub's
**[private vulnerability reporting](../../security/advisories/new)** for this repository, describe what you found and
how to reproduce it, and you'll get a reply as soon as possible.

## How Postarr protects your server

Postarr holds your media server token and API keys, so it's built to keep them in:

- **Login required.** Postarr is locked until a login is created on first run, and every page and API call needs a
  valid session after that. Wrong passwords lock the address out for 5 minutes after 5 tries. Behind a reverse proxy
  or Cloudflare tunnel on the same machine, it locks out the real client address, not the proxy's.
- **Sessions:** signed with a random key kept outside the database (`session.key` in the data folder), so a copy of the
  database or a downloaded backup can't be used to sign in. Signing out ends that session even if the cookie was
  copied. Cookies are `HttpOnly` and `SameSite=Lax`, and `Secure` over HTTPS.
- **Account changes** (password, username, turning login off) need the current password.
- **Secrets stay hidden.** API keys and tokens are never sent back to the page or written to logs. A saved token or
  key is only sent to the server address it was entered for: point Postarr at a different address and the token must
  be typed again.
- **Requests from other sites are refused.** A page on another site, port or subdomain can't make your browser change
  anything in Postarr (cross-site request forgery). Postarr can't be shown inside another site's frame
  (clickjacking).
- **Artwork downloads are limited** to TMDB, FanArt.tv, TheTVDB, Kometa's GitHub images and your own media server, so
  Postarr can't be used to reach other machines on your network. Uploads are checked and re-saved as JPEG, and
  oversized "decompression bomb" images are refused.
- **The Plex webhook** needs a secret token in its address.

## Good practice

- If you open Postarr to the internet, put it behind HTTPS (a reverse proxy or a Cloudflare tunnel). Over plain HTTP,
  the session cookie can be read by anyone on the network path.
- Create the login straight after installing. Until a login exists, whoever opens Postarr first creates it.
- Database backups (`Backups\Database`) contain your API keys. Keep downloaded copies somewhere private.
