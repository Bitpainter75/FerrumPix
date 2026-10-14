## 0.9.65

### What's new

- **Compare a single slider.** Right-click a slider or its number field in the editor to set it
  to its default for a moment; slider and field change colour, and the next right-click brings
  your value back. Several sliders can be compared at once, and in the number field SPACE does
  the same. Saving while comparing saves what you see, and before you move to another picture
  every slider gets its value back.

- **More cameras with their own base brightness.** Thirty more camera models, among them the
  Canon EOS 77D and M5, Fujifilm X-T2 and GFX 50S, Panasonic G9, Pentax K-3 Mark III and KP and
  several Sony Alpha models, now have their own value for the setting that matches the base
  brightness to the camera, and the Fujifilm X-T30 II has its own colours.

- **More from blown highlights.** Recovering highlights from the raw data now brings back more
  detail in bright skies, pink spots in recovered highlights turn neutral, and when you brighten
  a picture and pull the highlights down, recovered clouds keep their structure.

- **Faster detail sliders.** Sharpening, noise reduction, clarity, haze, vignette and the other
  detail and effect sliders respond faster, because the light and colour work underneath them is
  kept instead of being worked out again.

- **Lens correction for Sigma, Tamron and Tokina lenses on Canon.** Canon cameras store only the
  focal length for these lenses; FerrumPix now works out the lens from the camera's lens number,
  focal range and aperture, so the Tamron 17-50 and 18-200, the Sigma 10-20 and many more are
  recognised. Where the lens cannot be told apart, the correction for a Canon lens of the same
  focal length is no longer applied by mistake.

### Fixes

- Switching to another picture while depth blur was set but not applied no longer leaves the
  old picture's blurred preview over the new one.

- Copying, moving, duplicating or dragging a burst takes all of its pictures along, and a tag
  set on a burst reaches every picture in it; the white balance menu keeps its choice through
  undo and in saved edits.

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

