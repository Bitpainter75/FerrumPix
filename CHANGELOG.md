## Unreleased

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

### Fixes

- **More lenses are recognised.** Older Canon EOS, Olympus Four Thirds and Panasonic G bodies,
  Samsung NX, Sony A-mount and Pentax cameras, including Pentax DNG files, now name the lens they
  were used with, so lens correction finds its profile. A Leica DG lens no longer gets the profile of a Lumix lens of the same range.

- **Two single shots in the same second are no longer shown as a burst** when the camera recorded
  them as single shots.

- **Nikon lenses are recognised by name.** FerrumPix now knows about 600 Nikon F lenses, including
  many Sigma, Tamron and Tokina models, instead of guessing from focal length and aperture. Lens
  correction no longer mixes up an AF-S with an AF-P zoom, an autofocus 50 mm with a manual one, or
  a Sigma with the Nikon lens of the same range.

