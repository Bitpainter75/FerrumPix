# FerrumPix in the browser with Docker or Podman

FerrumPix can run on a home server or a NAS and show itself in a web browser on every other
device. What you get:

- the full FerrumPix, gallery, viewer and editor, in the browser of a laptop or desktop,
- a simple photo app for phones and tablets, which can be added to the home screen over HTTPS,
- accounts for family and friends, each with rights per folder,
- share links for people without an account,
- one catalog on the server, so every device sees the same ratings, keywords, people and edits.

The pictures stay on the server. The image is `ghcr.io/bitpainter75/ferrumpix`, for amd64 and
arm64, and works with Docker as well as with Podman.

## Which template

| Template | For | Address |
|---|---|---|
| `docker-compose.yml` with `Caddyfile` | any Linux server, all your devices, HTTPS through Caddy | `https://photos.example.org` |
| `docker-compose.synology.yml` | a Synology NAS, HTTPS through the reverse proxy DSM already has | `https://photos.example.org` |
| `docker-compose.synology-http.yml` | a Synology NAS in a home network you trust, without HTTPS | `http://<address of the NAS>:14500` |
| `docker-compose.local.yml` | only the computer it runs on, nothing else can reach it | `http://127.0.0.1:14501` |

Take HTTPS when you can: only then can the page for phones go onto the home screen, pictures be
copied to your device, and share links be opened from outside. Each template repeats its steps in
the comments at the top.

## The first start

Whatever the template: right after the first start, open the page yourself. The first visit is the
setup: a user name, an email address if you like (it is used for Forgot your password) and the
password twice. The password needs at least 8 characters with an uppercase and a lowercase letter,
a digit and a special character (a space counts). Until then the page shows nothing but this setup,
and whoever opens it first becomes the administrator. No password goes into a template or an
`.env`; FerrumPix keeps it only as a hash in the folder `data`.

When you sign in, you choose between the full desktop interface and the page for phones. Only the
administrator gets the desktop interface and the settings.

## With Caddy and HTTPS

1. Create a folder, for example `~/ferrumpix`, and put `docker-compose.yml` and `Caddyfile` into it.
2. In that folder, create the folder `data` for everything FerrumPix keeps. It has to belong to
   user id 1000:

       mkdir data
       sudo chown 1000 data

3. Create a file `.env` next to them:

       FERRUMPIX_DOMAIN=photos.example.org
       FERRUMPIX_PHOTOS=/path/to/photos

4. Start it: `docker compose up -d`
5. Open `https://photos.example.org` and set up your account (see [The first start](#the-first-start)).
6. In Settings, User management, enter `https://photos.example.org` as the Address of FerrumPix in the browser.

Caddy gets the certificate by itself when the name points at the server from the internet and the
ports 80 and 443 reach it. Without a public name, uncomment `tls internal` in the `Caddyfile`; the
browser then has to trust Caddy's own certificate once.

## Synology with HTTPS

1. In File Station, create `/volume1/docker/ferrumpix` and in it the folder `data`. Note the user id
   and group id of the DSM user who owns it (over SSH: `id <user>`; the first DSM user is usually
   1026, the group `users` is 100). This user also needs to read your photo folder.
2. Put `docker-compose.synology.yml` into `/volume1/docker/ferrumpix`, and next to it a file `.env`:

       FERRUMPIX_UID=1026
       FERRUMPIX_GID=100
       FERRUMPIX_PHOTOS=/volume1/photo

3. In Container Manager, Project, Create, choose `/volume1/docker/ferrumpix` and use the template.
4. In Control Panel, Login Portal, Advanced, Reverse Proxy, create an entry:
   - Source: **HTTPS**, your hostname (for example `photos.example.org`), port 443
   - Destination: HTTP, `localhost`, port 14500
   - Custom Header: *Create*, *WebSocket*. That is all; DSM passes on the remaining forwarding
     headers by itself.
5. Under Control Panel, Security, Certificate, pick the certificate for that hostname.
6. Open `https://photos.example.org` and set up your account.
7. In Settings, User management, enter `https://photos.example.org` as the Address of FerrumPix in the browser.

The source of the proxy entry has to be HTTPS. FerrumPix in this template only accepts requests
that the proxy marks as encrypted, and it signs the browser in with a cookie that browsers only
keep over HTTPS. With HTTP as the source, FerrumPix either refuses the request or the sign-in does
not stick, so the password never travels unencrypted. Opening `http://<NAS>:14500` directly does
not work either: the port is only open on the NAS itself, for the proxy.

Only if FerrumPix still answers 403 and its log shows `X-Forwarded-Proto none`, add a custom header
`X-Forwarded-Proto` with the value `https` to the proxy entry by hand.

## Synology in the home network, without HTTPS

1. to 3. as above, with `docker-compose.synology-http.yml`.
4. Open `http://<address of the NAS>:14500`, for example `http://192.168.1.10:14500`, and set up
   your account. Use the IPv4 address or the local name of the NAS: over its global IPv6 address a
   device looks like one from the internet and gets 403.

The connection is not encrypted: password and pictures travel readable through your network.
FerrumPix then only answers devices with private addresses, and the page shows that the connection
is unencrypted. The template uses the network of the NAS directly, so port 14500 has to be free
there.

## Only on this computer

1. Next to `docker-compose.local.yml`, create the folder `data`:

       mkdir data
       sudo chown 1000 data

2. Start it with your photo folder:

       FERRUMPIX_PHOTOS=/path/to/photos docker compose -f docker-compose.local.yml up -d

3. Open `http://127.0.0.1:14501` and set up your account.

## Users and rights

In the desktop interface, Settings, User management, the administrator adds further users with a
name, a password and, if they like, an email address, and gives them rules per folder:

| Right | Allows |
|---|---|
| View | see and download |
| Contribute | also rate, mark, upload and share |
| Manage | also rename and delete |

A rule applies to the folder and everything below it. Other users only get the page for phones and
see nothing outside their folders, and each picks a language for it; ratings and marks are shared.
A user without the administrator role sees nothing until they get a rule.

Every user name can be changed, the administrator's too: by the administrator in User management,
and by each user for their own account on the page for phones under More, User name. From then on
only the new name signs in, also for tools with `Authorization: Basic`; open sessions, rules,
shares and the second factor stay. An authenticator app keeps showing the old name, its codes
still work.

### Second factor

Every user can protect their sign-in with a code from an authenticator app (6 digits, changing
every 30 seconds): on the page for phones under More, Second factor, the administrator also in
Settings, User management. Scan the QR code or type in the key, then enter a code; only then is it
on. FerrumPix shows ten recovery codes once, each good for one sign-in without the app; save them
as a text file. Turning it off needs the current password and a code.

- Signing in then asks for the code after the password. Forgot your password sets only a new
  password; the code is still needed afterwards.
- If someone loses their phone, the administrator resets the second factor in User management;
  their open sessions end, and they sign in with the password alone and can set it up again.
- Tools that send the password with `Authorization: Basic` (curl and the like) are refused for an
  account with a second factor. Give such tools an account of their own without one.
- Signing in at the computer where FerrumPix runs, without a browser, never asks for a code.

## Mail for Forgot your password

With a mail server and the address of FerrumPix, Forgot your password on the sign-in page sends a
link for a new password to the user's email address. Without either, only the administrator can
help. Set it up in Settings, User management:

- Address of FerrumPix in the browser, under Browser and mobile view: the address at which the
  others reach FerrumPix, for example `https://photos.example.org`. It is required: the link in
  the mail always starts with it and never with the address a request came from, which anyone
  could make up.
- Under Email for forgotten passwords: Server, Port and Encryption of your mail provider, User
  name and Password of the mail account, and Sender, the address the mails come from; empty
  means the user name of the account.

*Send test email* checks the settings before you save them.

Behind a proxy that rewrites the Host header to the inner address, sign-in only works with that
address filled in; Caddy and the reverse proxy of DSM keep the header as it is.

## Share links

A share link shows pictures to people without an account: one picture, a selection, a folder or a
search result. Create it on the page for phones or from the right-click menu of the gallery. Each
link can expire, have its own password, allow or forbid downloads and leave out place, people and
camera. My shares on the page for phones and the settings list the links with their views, change
or revoke them, or pause them all. Sharing needs at least the Contribute right, and a link only
works from outside through the public HTTPS address.

## The page for phones on the home screen

The administrator switches with Mobile view in the gallery; everyone else gets the page for phones
anyway. Over HTTPS, add it to the home screen from the browser menu (*Add to Home Screen* or
*Install app*), and it opens like an app of its own. Over plain HTTP the browser does not offer
that.

## Photo folders

Every photo folder becomes one line under `volumes` in the template, each below `/photos`:

    volumes:
      - ./data:/data
      - /srv/photos:/photos/Pictures
      - /srv/archive:/photos/Archive

FerrumPix runs as user 1000 in the container (on a Synology as the DSM user set in `.env`). The
photo folders must be readable for that user, and writable if FerrumPix should save edits, sidecar
files, uploads or exports there. Models are not part of the image; FerrumPix downloads them into
`/data` when you ask for them.

FerrumPix only shows `/photos` in the container: the folder tree starts there, every folder and
file chooser stays inside it, and saving, exporting, copying and moving only go there. `/photos` is
also its home and start folder. Deleting, renaming, moving and downloading work for everything
below it without any further switch; `/photos` itself cannot be renamed or deleted. To use folders
outside it, uncomment `FERRUMPIX_FOLDER_ROOTS` in the template, several separated by `:`, for
example `/photos:/archive`. Set it to an empty value to see the whole container again.

| Path | Content |
|---|---|
| `/photos/<name>` | your photo folders, one line per folder in the template |
| `/data` | settings, catalog, thumbnails and downloaded models; the folder `data` next to the template |
| `/data/share/FerrumPix/Fonts` | own fonts, inside the data folder, no separate folder (see [Own fonts](#own-fonts)) |

## Own fonts

The image already brings these free fonts, ready in the font list of the editor:

- DejaVu Sans, Serif and Sans Mono; Noto Sans and Noto Serif with Latin, Greek and Cyrillic, plus
  the other Noto scripts of the interface languages
- Liberation Sans, Serif and Mono, with the same widths as Arial, Times New Roman and Courier New,
  and Carlito and Caladea, with the same widths as Calibri and Cambria, so text laid out in those
  keeps its line breaks
- Roboto, Open Sans, Lato, Montserrat, Source Sans 3, Source Serif 4, Oswald, Nunito and Inter

There is no font for Chinese, Japanese or Korean. Everything else goes into the fonts folder.

Fonts you put into
`/data/share/FerrumPix/Fonts` can be chosen for text objects and text watermarks in the editor and
are used in exports, prints, Photoshop files and FerrumPix projects. TrueType and OpenType files
work (`.ttf`, `.otf`, `.ttc`, `.otc`); subfolders are fine.

In the folder `data` next to the template, create `share/FerrumPix/Fonts` and copy the font files
into it. Then click *Reload* next to the fonts folder in Settings, Catalog, or restart the container.

## Graphics card for the models

Optional: without a graphics card, everything runs on the processor. `docker-compose.yml` and
`docker-compose.local.yml` carry commented-out lines for it. Intel and AMD cards come in through
`/dev/dri`, NVIDIA cards through the NVIDIA Container Toolkit. Whether FerrumPix uses the card is
shown in Settings, Performance. Most Synology models have no suitable graphics unit.

## Language and local time

`FERRUMPIX_LANG` in `.env`, for example `de_DE.UTF-8`, sets language and date format for the first
start; later both can be changed in Settings. The image runs on UTC. To see your local time in
file dates and the log, uncomment the line `TZ: "Europe/Berlin"` under `environment` and set your
time zone there.

## Keep it safe

- HTTPS is the recommended way. Plain HTTP is only for a home network you trust, and only with the
  explicit switch `FERRUMPIX_BROWSER_ALLOW_HTTP=1` that the HTTP template sets.
- Never open the FerrumPix port in your router: no port forwarding, no UPnP. For access from
  outside, use HTTPS, or a VPN into your home network (WireGuard or OpenVPN) with private
  addresses.
- Leave the published ports as the templates set them. The local template and the Synology HTTPS
  template keep the port on `127.0.0.1` on purpose.
- Give other users only the folders they need, and View where that is enough.

## Updating

In the folder of the template:

    docker compose pull
    docker compose up -d

For the local template add `-f docker-compose.local.yml` to both; on a Synology run them over SSH
with `sudo` in `/volume1/docker/ferrumpix`. The folder `data` stays as it is. The templates follow
`:latest`; to stay on one version, put for example
`FERRUMPIX_IMAGE=ghcr.io/bitpainter75/ferrumpix:0.9.92` into `.env`.

## The administrator forgot the password

If no mail arrives either:

1. Add `FERRUMPIX_RESET_ADMIN_PASSWORD: "1"` under `environment` in the template.
2. `docker compose up -d`
3. Open the page right away and choose a new password for your existing user name. This also
   removes the second factor of the administrator, if there was one; the log says so. Other users
   and their folders stay as they are.
4. Remove the line again and run `docker compose up -d` once more. It works only once; while it is
   still set, later starts ignore it.

## Backup

Everything FerrumPix keeps is in the folder `data`: settings, catalog, users, shares, thumbnails
and models. To back it up, stop the container with `docker compose stop`, copy the folder and start
it again with `docker compose start`. Settings, Catalog, Backup writes catalog, settings,
favourites and saved searches into a single ZIP file as well, the sign-in passwords only if you tick
that. Your photos are not part of either; back them up as usual.

## Log

Besides the start lines, the log of the container holds one line per security event, each starting
with `FerrumPix security:`, the time in UTC, the event, the user (`-` if none) and the address the
request came from (behind the proxy the address of the browser, as the proxy passes it on):

    FerrumPix security: 2026-11-27T15:04:05Z event=signin.failed user=- from=192.168.1.20 via=page
    FerrumPix security: 2026-11-27T15:04:31Z event=signin.success user=anna from=192.168.1.20 via=page
    FerrumPix security: 2026-11-27T15:10:02Z event=upload user=anna from=192.168.1.20 files=12 folder=photos/2026/Holiday via=mobile
    FerrumPix security: 2026-11-27T16:00:40Z event=share.opened user=- from=203.0.113.7 share=3f9c2a1b

What is logged: sign-in (`signin.success`, `signin.failed`, without telling a wrong name from a
wrong password), the brake against guessing (`throttled`), sign-out, locking and unlocking
(`app.locked` with `reason=manual`, `idle` or `start`, `app.unlocked`), the first setup
(`setup.done`), the user name set at the first sign-in (`user.claimed`), the administrator reset at
start (`admin.reset`), Forgot your password (`password.reset.requested` with the sender only,
`password.reset.mailed` without the recipient, `password.reset.done`), changed passwords
(`password.changed`), users created, changed, renamed (`user.renamed` with `old` and `new`),
locked, deleted or made administrator (`user.*`),
folder rules (`rule.changed`), share links created, changed, renewed, revoked, paused and resumed
(`share.*`, `shares.paused`, `shares.resumed`), an opened share (once an hour per share and
address), a wrong share password (`share.password.failed`), ZIP downloads (`download.zip` with the
number of files), deleting and renaming on the phone page (`file.deleted`, `file.renamed`, paths
relative to the photo folder), uploads (`upload`, one line per batch with the number of files and
the target folder), the second factor (`twofactor.enabled`, `twofactor.disabled`, `twofactor.reset`
with `via=admin` or `via=start`, a wrong code as `twofactor.failed`, `twofactor.recovery.used` with
the number left, and `signin.refused` for a tool with Basic on such an account) and refused
requests (`request.refused`). Passwords, links, codes, cookies and
mail addresses never appear; a share shows only the first eight characters of its id.

To see only these lines:

    docker compose logs ferrumpix | grep "FerrumPix security"

With plain Docker the same works with `docker logs <name of the container>`. The lines also go into
the diagnostic log in the folder `data` when diagnostic logging is switched on in Settings.

## Troubleshooting

- **The log** tells most: `docker compose logs -f ferrumpix` (on a Synology in Container Manager,
  in the details of the container). It names the address FerrumPix listens on, whether the setup is
  still open, and every refused request with the reason (see [Log](#log)).
- **Not reachable from another device** with the local or the Synology HTTPS template: that is
  intended, the port is only open on `127.0.0.1`. Use the address of the proxy.
- **403 or a sign-in that does not stick on a Synology**: the source of the proxy entry is HTTP
  instead of HTTPS. If the log shows `event=request.refused` with `proto=none`, add the header by hand (see
  [Synology with HTTPS](#synology-with-https)); if it names an unexpected address, the subnet of
  the template is taken on your NAS.
- **403 in the home network without HTTPS**: the device came over a global IPv6 address; use the
  IPv4 address of the NAS.
- **FerrumPix does not come up or forgets its settings**: the folder `data` does not belong to user
  1000 (on a Synology to the user in `.env`). Fix it with `sudo chown -R 1000 data`.
- **No pictures**: the path in `FERRUMPIX_PHOTOS` is wrong or not readable for that user.
- **No certificate with Caddy**: `docker compose logs caddy`; the name has to point at the server
  and the ports 80 and 443 have to reach it.
