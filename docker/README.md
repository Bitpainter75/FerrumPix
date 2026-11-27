# FerrumPix in the browser with Docker or Podman

FerrumPix can run on a home server and show itself in a web browser on any other device: the same
gallery, viewer and editor, with the pictures staying on the server. The image is
`ghcr.io/bitpainter75/ferrumpix`, for amd64 and arm64.

This folder holds ready-made Compose templates. Copy the one that fits into a folder of its own,
fill in the few values it asks for and start it.

| Template | For |
|---|---|
| `docker-compose.yml` with `Caddyfile` | other devices, with HTTPS through Caddy in front of FerrumPix |
| `docker-compose.local.yml` | the computer FerrumPix runs on, nothing else can reach it |
| `docker-compose.synology.yml` | a Synology NAS, with HTTPS through the reverse proxy DSM already has |
| `docker-compose.synology-http.yml` | a Synology NAS without HTTPS, for a home network you trust |

Each template explains its steps in the comments at the top. In short:

## With Caddy and HTTPS

1. Put `docker-compose.yml` and `Caddyfile` into a folder.
2. Next to them, create `ferrumpix-password.txt` with the password in the first line, readable for
   user id 1000 (`chown 1000 ferrumpix-password.txt; chmod 600 ferrumpix-password.txt`).
3. Create an `.env` with `FERRUMPIX_DOMAIN=photos.example.org` and
   `FERRUMPIX_PHOTOS=/path/to/photos`.
4. Create an empty folder `data` next to them for everything FerrumPix keeps, owned by user id
   1000 (`mkdir data; sudo chown 1000 data`).
5. `docker compose up -d`, then open `https://photos.example.org`.

Caddy gets the certificate by itself when the name points at the server from the internet. Without
a public name, uncomment `tls internal` in the `Caddyfile`; the browser then has to trust Caddy's
own certificate once.

## Only on this computer

    mkdir data; sudo chown 1000 data
    FERRUMPIX_PHOTOS=/path/to/photos docker compose -f docker-compose.local.yml up -d

Then open `http://127.0.0.1:14501`. A password is optional here (`FERRUMPIX_PASSWORD` in the
environment).

## Synology with HTTPS

1. Create `/volume1/docker/ferrumpix/data` and `/volume1/docker/ferrumpix/password.txt` in File
   Station.
2. Put the template and an `.env` with your user id, group id and photo folder into
   `/volume1/docker/ferrumpix`.
3. Create a project from that folder in Container Manager.
4. Add a reverse proxy entry in DSM from HTTPS port 443 to `http://localhost:14500`, with the
   WebSocket headers and `X-Forwarded-Proto` and `X-Forwarded-Host`, as the template describes.
5. Open `https://photos.example.org` and sign in.

## Synology in the home network, without HTTPS

The same first three steps, then open `http://<address of the NAS>:14500`. The connection is not
encrypted: password and pictures travel readable through your network. FerrumPix then only answers
devices with private addresses, refuses to start without a password, and the page shows that the
connection is unencrypted.

## The password

Every template that lets other devices in needs a password, and FerrumPix does not start without
one. It needs at least 8 characters with an uppercase and a lowercase letter, a digit and a special
character (a space counts). These templates read it from a file handed in as a secret, so it is
neither in the Compose file nor in the list of running programs. Every browser sees FerrumPix's own
sign-in window first.

## Keep it safe

- HTTPS is the recommended way. Plain HTTP is only for a home network you trust, and only with the
  explicit switch `FERRUMPIX_BROWSER_ALLOW_HTTP=1` that the HTTP template sets.
- Never open the FerrumPix port in your router: no port forwarding, no UPnP. For access from
  outside, use HTTPS, or a VPN into your home network (WireGuard or OpenVPN) with private
  addresses.
- Leave the published ports as the templates set them. The local template and the Synology HTTPS
  template keep the port on `127.0.0.1` on purpose.

## Graphics card for the models

Optional: without a graphics card, everything runs on the processor. `docker-compose.yml` and
`docker-compose.local.yml` carry commented-out lines for it. Intel and AMD cards come in through
`/dev/dri`, NVIDIA cards through the NVIDIA Container Toolkit. Whether FerrumPix uses the card is
shown in Settings, Performance. Most Synology models have no suitable graphics unit.

## Own fonts

A container brings only a few fonts of its own, far fewer than a desktop. Fonts you put into
`/data/share/FerrumPix/Fonts` can be chosen for text objects and text watermarks in the editor and
are used in exports, prints, Photoshop files and FerrumPix projects. TrueType and OpenType files
work (`.ttf`, `.otf`, `.ttc`, `.otc`); subfolders are fine.

In the folder `data` next to the template, create `share/FerrumPix/Fonts` and copy the font files
into it. Then click *Reload* next to the fonts folder in Settings, Catalog, or restart the container.

## Folders in the container

| Path | Content |
|---|---|
| `/photos/<name>` | your photo folders, one line per folder in the template |
| `/data` | settings, catalog, thumbnails and downloaded models; the folder `data` next to the template |
| `/data/share/FerrumPix/Fonts` | own fonts, inside the data folder, no separate folder (see [Own fonts](#own-fonts)) |

FerrumPix runs as user 1000 in the container (on a Synology as the DSM user set in `.env`). The
photo folders must be readable for that user, and writable if FerrumPix should save sidecar files
or exports there. Models are not part of the image; FerrumPix downloads them into `/data` when you
ask for them.
