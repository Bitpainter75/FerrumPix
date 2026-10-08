## 0.9.64

### What's new

- **Lens correction for compact cameras.** RAW files from cameras with a built-in lens, such as
  the Panasonic LX and FZ, Canon PowerShot, Sony RX and Fujifilm X100 series, are now corrected
  for distortion, coloured fringes and vignetting.

- **Lens correction from the camera's own data.** Sony, Olympus, OM System and Panasonic cameras
  store their distortion correction, most of them also their colour fringe correction, in the RAW
  file. Where the lens database has no profile, FerrumPix now uses
  that data, and the lens panel says so.

- **Vignetting follows the focus distance.** Where the camera records how far it was focused,
  the edge darkening is corrected for that distance instead of always for infinity, which helps
  close-ups in particular.

- **A real blur.** The Blur group now has a slider that softens the whole picture evenly, from a
  gentle touch to a strong blur, and it looks the same in the preview as in the export. The
  slider that used to be called Blur is now called Smoothing, because that is what it does: it
  reduces noise and leaves edges and detail alone.

- **Recalibrated sliders.** Contrast, Highlights, Shadows, Whites, Blacks, Vibrance, the
  luminance of the colour mixer, the brightness in colour grading and the vignette now have a
  more natural strength and range. Colour grading tints more evenly, the colour mixer bands sit
  where presets expect them, and tone and channel curves shape colours the way presets were made
  for, so presets with curves no longer drift red or green. Highlights and Shadows also take the
  brightness of the whole picture into account: in a bright picture Shadows open up dark areas
  further, in a dark one they stay gentler. Pictures you already edited keep their look.

### Fixes

- **Highlights, Shadows, Whites and Blacks no longer turn tones around.** Pulling highlights
  down and shadows up together, especially at full strength, could make the midtones run
  backwards; brighter parts of a gradient came out darker than the parts next to them. Strong
  settings also keep colours on their hue now, so an orange stays orange instead of turning a
  greyish yellow.

- **More lenses are recognised.** Older Canon EOS, Olympus Four Thirds and Panasonic G bodies,
  Samsung NX, Sony A-mount and Pentax cameras, including Pentax DNG files, now name the lens they
  were used with, so lens correction finds its profile. A Leica DG lens no longer gets the profile of a Lumix lens of the same range.

- **Two single shots in the same second are no longer shown as a burst** when the camera recorded
  them as single shots.

- **Nikon lenses are recognised by name.** FerrumPix now knows about 600 Nikon F lenses, including
  many Sigma, Tamron and Tokina models, instead of guessing from focal length and aperture. Lens
  correction no longer mixes up an AF-S with an AF-P zoom, an autofocus 50 mm with a manual one, or
  a Sigma with the Nikon lens of the same range.

