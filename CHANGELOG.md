## FerrumPix 0.9.38

### What's new

- **The lens database is current again.** Lens correction now knows around 1550 lenses and 1050
  camera bodies including recent camera models.

- **The Windows packages and the Flatpak carry a newer LibRaw.** It recognises a good seventy
  camera models more than the one before, so more recent cameras develop from their sensor data
  instead of falling back to the embedded preview. The Windows build now also opens lossy
  compressed DNG files, which stayed closed there while Linux opened them.

- **You can choose how RAW files are demosaiced.** RAW development settings now offer AHD, DCB and
  PPG: the default balances detail and speed, DCB draws finer at the cost of more colour noise and
  clearly more time, PPG is the quick one. Thumbnails rebuild themselves after a change.

- **An experiment for wide-gamut displays on macOS.** On a Display P3 screen everything looks too
  strong, photos and interface alike, because the window is drawn without a colour space and macOS
  reads the numbers as screen values. A switch under Appearance tells the system that FerrumPix
  draws in sRGB, so macOS converts for whichever screen the window is on. Whether it takes effect
  depends on the drawing path underneath, so it is off by default, and the settings show what the
  last attempt did. Takes effect after a restart.

- **Application scaling is set per display.** On Linux, where the system does not scale each
  monitor by itself, every connected display now has its own slider instead of one value plus a
  choice of which display it applies to, so the interface does not end up smaller on a
  high-resolution display than on the one next to it. A value for a display that is currently
  unplugged is kept and marked as such, so it is still there when you plug it back in.

- **Gallery filters survive a folder change.** Rating, favourite, file type and colour label stay as
  you set them while you walk through folders, so a series can be gone through with the same
  selection in view. They still reset when you change source, to Immich, Nextcloud or a search,
  where nobody chose them for what you are now looking at.

### Fixes

- **Lens matching no longer falls for a lens of a different make.** When your lens is not in the
  database, a third-party lens with the same focal length and aperture could win the match and
  bend the picture with the wrong curve. It now stays out unless the shot data names that maker.

- **Gallery tiles of RAW files are no longer demosaiced with the weakest method.** In one path the
  quality setting was reset to linear interpolation instead of the normal one.

- **On macOS, RAW development and HEIC now work with libraries installed through Homebrew.**
  FerrumPix looks in Homebrew's library folders for LibRaw and libheif, as it already did for mpv.
  On Apple Silicon those folders lie outside the paths macOS searches by itself, so an installed
  LibRaw stayed invisible and RAW files quietly fell back to the embedded preview.
