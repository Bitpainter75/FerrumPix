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

### Fixes

- A location, copyright or title that was stored only in a sidecar file, for example after
  setting the location of a raw file, did not reach the exported JPEG.

- Save as in the editor ignored the EXIF switch and the copyright notice entered in the dialog.

- In the Flatpak, videos did not play and showed no thumbnails; the viewer said mpv was not
  installed, although it is part of the Flatpak.

- Denoising a raw file with the model darkened the edges of bright areas that were brought back
  from overexposure, such as a sky or a lamp. They now stay as they were.

- Some Windows downloads of the last two updates were missing the part that runs the AI models,
  so the AI functions did not work there.

- On Windows computers with an older Microsoft Visual C++ runtime, the AI functions could crash
  FerrumPix while loading. The Windows downloads now bring the runtime they need. If an older one
  is already in use anyway, the AI functions stay off instead of crashing.

- On some Windows computers FerrumPix closed right away at every start, because the part that
  runs the AI models crashed while loading. That part now loads only when you use, switch on or
  check an AI function, so FerrumPix starts in any case. If it crashes there, FerrumPix remembers
  it and shows a message the next time instead of crashing again; a new version tries it again.

- On Linux, sidecar files with an upper case ending such as IMG_1.XMP were overlooked. Rating,
  label and keywords in them were not read, FerrumPix created a second sidecar file next to them,
  and they stayed behind when the picture was moved, copied, renamed or deleted. They are now
  found, written to and carried along like any other sidecar file.

- Writing the names of people into a sidecar file removed other marked areas that another
  program had stored there, such as pets, focus points or faces without a name. Only the named
  faces are replaced now, everything else stays as it was.

