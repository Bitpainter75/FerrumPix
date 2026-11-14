## 0.9.83

### Changes

- The gallery can show the pictures from all subfolders of a folder as well. A new button left of
  the search field switches this on and off; searching and filtering then cover the subfolders too.

- FerrumPix can be used in a web browser. It is the same application with everything it can do; it
  just opens no window on the screen and shows itself in a browser tab instead. That is useful on a
  computer without a screen, such as a home server next to your photos. This is a first version:
  copy and paste stay inside FerrumPix, and videos play without sound.

  **On the same computer.** Start FerrumPix with `--browser` and open the address it prints:

  ```
  FerrumPix --browser
  ```

  FerrumPix is then at `http://127.0.0.1:14500` and fills the tab. Reloading the tab or coming back
  later shows the same session, and one browser can be connected at a time.

  **With a sign-in.** Give a password, and a user name if you like, and the browser asks for them
  before it shows anything:

  ```
  FerrumPix --browser --browser-user=anna --browser-password=secret
  ```

  **On another port.** `--browser-bind` sets address and port:

  ```
  FerrumPix --browser --browser-bind=127.0.0.1:8080
  ```

  **From another device**, such as a tablet or a laptop in the same network. FerrumPix itself does
  not encrypt the connection, and a password sent over the network could be read along. So it only
  accepts other devices through a reverse proxy that provides HTTPS. 
  Tell FerrumPix that the proxy is there with `--browser-tls-proxy`; a password is required then:

  ```
  FerrumPix --browser --browser-tls-proxy --browser-user=anna --browser-password=secret
  ```

  The proxy runs on the same computer and forwards `https://photos.example.home` to
  `127.0.0.1:14500`, including the WebSocket at `/ws`. Anyone who tries to reach FerrumPix past
  the proxy is turned away before being asked for the password. Without `--browser-tls-proxy`,
  FerrumPix refuses to start on an address other than its own computer and says why.

  If the proxy runs on a different computer, FerrumPix has to listen on the network and needs to
  know which computer the proxy is, because it only believes what comes from there:

  ```
  FerrumPix --browser --browser-tls-proxy --browser-bind=0.0.0.0:14500 --browser-proxy-address=192.168.1.2 --browser-user=anna --browser-password=secret
  ```

  **With the settings in the environment.** Every setting can come from an environment variable
  instead of the command line. That keeps the password out of the process list, where a start
  parameter can be read by everyone on the computer, and it is the usual way for a service or a
  container:

  ```
  env FERRUMPIX_BROWSER_USER=anna FERRUMPIX_BROWSER_PASSWORD=secret FerrumPix --browser
  ```

  The example from above with the proxy on a different computer looks like this:

  ```
  env FERRUMPIX_BROWSER_BIND=0.0.0.0:14500 FERRUMPIX_BROWSER_TLS_PROXY=1 FERRUMPIX_BROWSER_PROXY_ADDRESS=192.168.1.2 FERRUMPIX_BROWSER_USER=anna FERRUMPIX_BROWSER_PASSWORD=secret FerrumPix --browser
  ```

  | Start parameter | Environment variable |
  |---|---|
  | `--browser-bind=127.0.0.1:8080` | `FERRUMPIX_BROWSER_BIND=127.0.0.1:8080` |
  | `--browser-user=anna` | `FERRUMPIX_BROWSER_USER=anna` |
  | `--browser-password=secret` | `FERRUMPIX_BROWSER_PASSWORD=secret` |
  | `--browser-tls-proxy` | `FERRUMPIX_BROWSER_TLS_PROXY=1` |
  | `--browser-proxy-address=192.168.1.2` | `FERRUMPIX_BROWSER_PROXY_ADDRESS=192.168.1.2` |

  `--browser` itself always stays on the command line, and a start parameter wins over the
  variable of the same setting.

- On some newer Linux systems FerrumPix ended right at startup, before a window appeared. It starts
  there now.

- When deleting is refused because the pictures are outside the home folder, the message now says
  where this can be allowed in the settings.

- The healing brush continues lines that run through the repaired spot, such as a treeline or the
  horizon, instead of pulling the ground up into the sky or leaving a bright patch in the trees.
  Where no fitting part of the picture is found, it repairs as before.

- A new setting under Editor decides where the home button in the editor leads: back to where the
  editor was opened from, as before, or always to the gallery. ESC still goes back one step.

- The Metadata menu can now remove the AI keywords or the people of the selected pictures, and it
  offers Find faces for the selection as well. Removed keywords and faces do not come back on their
  own; Analyse AI keywords again and Find faces bring them back on request.

- RAW, HEIC and JPEG XL now always open with the libraries that come with FerrumPix, also where the
  system has its own. Every computer develops a RAW the same way, and cameras the bundled RAW
  library knows work everywhere.

- When zoomed in, the info panel of the viewer, the full screen view and the editor shows a
  navigator at the bottom: the picture small, with a frame around the part you are looking at. Drag
  the frame, or click beside it, to look at another part.

- Raw files from cameras that let you choose the aspect ratio, such as 3:2, 16:9 or 1:1 on Panasonic
  cameras, open in the editor in the format chosen on the camera. A new switch under Lens correction
  shows the whole sensor again. Pictures you edited before keep the whole sensor.

- Auto is more careful with pictures taken at high ISO: it brightens them less and keeps more of
  the colour of the light, so the mood of a dim indoor scene stays and the noise stays down.

### Fixes



