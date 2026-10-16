## 0.9.66

### What's new

- **RAW development on macOS without Homebrew.** The Mac packages now bring their own LibRaw, so
  RAW files are developed from the sensor data even if nothing is installed. A LibRaw from
  Homebrew is still preferred when it is there.

- **Compare a single curve.** Right-click RGB, R, G, B or L above the tone curve to set that
  channel's curve back to a straight line for a moment; the next right-click brings it back,
  just like comparing a single slider.

### Fixes

- Enlarging the canvas no longer makes the photo shrink inside the editor. Even one extra pixel
  could show the picture at half its size with a huge border around it; the saved image was
  always right, now the editor shows the same.

- Scans saved as TIFF or DNG are read far less often: reading their details no longer loads the
  whole file, so indexing watched folders, opening a picture in the editor and the info panel are
  much quicker on a network drive. A film scan as DNG used to travel over the network up to four
  times, now about once.

- The Flatpak and the Microsoft Store version no longer look for a newer version on GitHub;
  updates come through the software manager or the Store that installed them.

- In the Flatpak, pictures in a folder you shared through the system's folder dialog can be
  renamed, moved and deleted again, like on a second drive.

- In the Flatpak, the window buttons follow your desktop's order again instead of standing the
  wrong way round, and the folder tree shows your drives instead of the inside of the sandbox.

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

- **Videos in the Flatpak.** The Flatpak now brings its own video player along, so videos get
  their thumbnails in the gallery and play in the viewer, with sound.

### Fixes

- Switching to another picture while depth blur was set but not applied no longer leaves the
  old picture's blurred preview over the new one.

- Bright, almost white areas in RAW files, such as lit fluorescent tubes or a sunny patch, no
  longer turn pink, also without recovering highlights.

- A RAW saved as JPEG, PNG or another format keeps the name of its lens, also where the camera
  stores the lens only in its own data.

- Copying, moving, duplicating or dragging a burst takes all of its pictures along, and a tag
  set on a burst reaches every picture in it; the white balance menu keeps its choice through
  undo and in saved edits.
