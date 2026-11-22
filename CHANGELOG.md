## Unreleased

### Changes

- Exporting to TIFF and PNG is much faster: the file is now compressed on all processor cores
  instead of one. The pixels stay the same.

- HEIC and HEIF pictures open faster on computers with many processor cores.

- RAW files open and export noticeably faster on computers with several processor cores: a
  step of the RAW development that ran on one core now uses all of them. The picture stays
  exactly the same.

- 16-bit exports from RAW files are quicker, because the RAW development no longer runs twice
  for the 8-bit and the 16-bit picture.

- Straighten, perspective, rotate, flip and the warp grid now use all processor cores. Moving a
  slider on a straightened or rotated picture responds noticeably faster, and so do growing,
  shrinking, bordering and refining a selection and the outline and fill of a selection layer.

- Shadow and glow of a selection layer are drawn on all processor cores, so a picture with such a
  layer reacts much faster. Glow on objects is quicker as well.

### Fixes

- A damaged or crafted Sony ARW file no longer crashes the program.

- The first start with a new picture library, or after an update that extends it, no longer
  fails now and then with a database error when two parts of the program open it at once.

- If the graphics driver crashes the program while a model runs on the graphics card, that model
  stays on the processor for this card from the next start on, until a new driver or a new
  program version arrives. The settings show which model is affected and can lift the block.

- WebP pictures get their gallery tile again when the thumbnail cache is switched off or the
  picture lies in a temporary folder.

- The mouse wheel scrolls the filmstrip in the editor again when the filmstrip sits on the left
  or right.

- On Linux and macOS, saving over an existing picture or sidecar file keeps its access rights, so
  a private photo stays private, and saving through a symbolic link updates the file it points to
  instead of replacing the link.

- Batch processing, pasting and moving in the gallery no longer let "Overwrite all" replace a
  picture written earlier in the same run or another picture of the selection, for example the
  camera JPEG next to its RAW, or the first of two "photo.jpg" from different folders, which
  moving could lose for good. Such a file now gets a new name like "photo (1).jpg" instead.
  The same goes for "photo.cr2" and "photo.nef" from different folders, whose XMP files would
  meet as "photo.xmp", and a foreign XMP file already in the destination now only skips that one
  picture instead of stopping the rest.

- If pasting or moving a picture is interrupted, for example by a crash, the half-copied file no
  longer stays hidden in the destination folder: the next paste or move into that folder removes
  it.

- A damaged LUT file no longer causes an error every time the picture is drawn; it simply has no
  effect.

- Saving an edit beside a picture no longer silently overwrites what another FerrumPix window,
  another computer or a sync service changed in the meantime: ratings, labels and keywords from
  there stay, and a different edit is kept next to the picture instead of being lost.

- A preset with a single broken value now loads with all its other settings instead of failing
  with an arithmetic overflow.

- Damaged PSD files and Canon CR2 files with impossible sizes are rejected cleanly.
