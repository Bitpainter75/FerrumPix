## Unreleased

### Changes

- The Windows setup offers a desktop shortcut on its last page, ticked by default.

- Save as, Export and the batch dialogs have three more switches under Take over: description,
  copyright and location. Each switch also decides what ends up in the new file. Switched on, it
  brings along what FerrumPix shows for the source, even when it only lives in a sidecar file.
  Switched off, it leaves that out. Rating, label and keywords now work the same way. The
  switches come back the way you left them last time.

- New setting under RAW development: keep denoised raw files. When a raw file is denoised with the
  model, the result is stored next to it as a .fpdnz file when you save, export or run a batch.
  Opening or exporting that picture again no longer has to wait for the model, and the strength
  can still be changed. Saving without denoising removes the file, and it moves, copies and goes
  to the trash together with the raw file. Switching the setting off keeps existing files. The
  files are large, about 60 MB for a 24 megapixel picture.

- The symbol on the Denoise with model button lights up in the accent colour while the picture
  is denoised with the model.

- Moving a large text or watermark in the editor is quicker, most of all when it uses a blend
  mode: only the area its letters and effects cover is redrawn, not its whole frame.

### Fixes

- A location, copyright or title that was stored only in a sidecar file, for example after
  setting the location of a raw file, did not reach the exported JPEG.

- Save as in the editor ignored the EXIF switch and the copyright notice entered in the dialog.

- In the Flatpak, videos did not play and showed no thumbnails; the viewer said mpv was not
  installed, although it is part of the Flatpak.

- Denoising a raw file with the model darkened the edges of bright areas that were brought back
  from overexposure, such as a sky or a lamp. They now stay as they were.

- AI functions on Windows: some downloads of the last two updates were missing the part that runs
  the AI models, and on computers with an older Microsoft Visual C++ runtime that part could crash
  FerrumPix, on some even at every start. The Windows downloads now bring everything they need,
  including the runtime, and the AI part loads only when you use, switch on or check an AI
  function, so FerrumPix always starts. If it still cannot load, the AI functions stay off and
  FerrumPix shows a message instead of crashing; a new version tries it again.

- On Linux, sidecar files with an upper case ending such as IMG_1.XMP were overlooked. Rating,
  label and keywords in them were not read, FerrumPix created a second sidecar file next to them,
  and they stayed behind when the picture was moved, copied, renamed or deleted. They are now
  found, written to and carried along like any other sidecar file.

- Writing the names of people into a sidecar file removed other marked areas that another
  program had stored there, such as pets, focus points or faces without a name. Only the named
  faces are replaced now, everything else stays as it was. Named faces that another program
  added to the sidecar file are taken into FerrumPix first, so writing no longer removes them.
  Faces you removed or renamed in FerrumPix do not come back from the file.

- Raw files from several DJI drones and action cameras, and from the Hasselblad cameras built by
  DJI, had dark corners, some with a cyan tint, and came out too bright. FerrumPix now uses the
  lens corrections these cameras store in the file, so shading, colour fringes and distortion are
  corrected the way the camera intends. They follow the lens correction switches like any other
  lens correction.

- The same now applies to DNG files from several iPhones up to the XS, which had dark corners,
  and from the Pixel 3a, 6 Pro, 7 Pro and 8 Pro and the Leica CL, Q, Q2 and SL3-P, which kept
  part of the distortion of their lens. On the Leica Q2 the corners also no longer come out too
  bright, since the camera has already evened them out.

- Raw files from the Hasselblad H4D came out too bright.

