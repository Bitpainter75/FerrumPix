## Unreleased

### Changes

- Denoising with a model works on raw files directly on the raw data, before exposure and color.
  A raw file stays a raw file: nothing is baked in, and every slider keeps working on the
  denoised picture. It keeps more detail and shifts colors less than before, also on Fujifilm
  X-Trans files, and takes seconds on a graphics card. Changing the strength afterwards needs no
  new run. When a picture with saved denoising is opened, you are asked before it is computed.
  Applying adjustments in a batch with overwriting now also denoises raw files, by saving it with
  their edits. The automatic strength is tuned for raw files separately, so very noisy shots get
  the full strength they need.

- Denoising raw files takes away more of the remaining noise at high ISO, and dark areas keep
  their brightness without coarse color blotches. This comes with a new model file; the settings
  offer it as an update, and the previous one keeps working until then.

- Finished pictures such as JPEG, PNG or PSD are denoised into the pixels as before, with the
  faster of the two former options. The slower one is gone. Edits saved with it are switched
  over when you open the picture, so a raw file may look slightly different than before.

- A # in a file name is always a counter, also without other placeholders. Save as picks the next
  free number in the target folder, so saving again gives _002 instead of asking about _001.

- Save as remembers the last name pattern and selects it again next time, like the target folder.
  The import also brings back the last target folder and a name pattern you wrote yourself.

- Nikon raw files in the High Efficiency format (HE and HE*, for example from the Z6 III, Z8, Z9
  and Zf) now open as raw files on Windows, macOS and in the Flatpak. Before, only their embedded
  preview was shown.

- Export, convert, resize, apply adjustments and apply watermark can upload to Nextcloud: each
  picture goes up as a new file next to its original, or into the upload folder from the settings.

### Fixes

- Choosing Nextcloud as the target in a batch dialog saved the files into the local folder instead.

- Save as to Nextcloud with a # counter takes the next free number on the server, and uses the
  upload folder from the settings instead of always /Photos.

- Apply watermark took over the file name pattern left behind by the previous dialog.

- On Windows, the JPEG XL support is updated with the latest security fixes.

- Copying a picture or folder over an existing one with Overwrite kept the rating, label, keywords
  and people of the replaced picture. The copy now carries those of the original.

- If overwriting fails and a companion file of the old picture cannot be put back, the message now
  says that the picture itself is back and where the companion file is.


