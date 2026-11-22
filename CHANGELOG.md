## Unreleased

### Changes

- Exporting to TIFF and to 16-bit PNG is much faster: the file is now compressed on all processor
  cores instead of one. The files and their pixels stay the same.

- 16-bit exports from RAW files are quicker, because the RAW development no longer runs twice
  for the 8-bit and the 16-bit picture.

- Straighten, perspective, rotate, flip and the warp grid now use all processor cores. Moving a
  slider on a straightened or rotated picture responds noticeably faster, and so do growing,
  shrinking, bordering and refining a selection and the outline and fill of a selection layer.

### Fixes

- The mouse wheel scrolls the filmstrip in the editor again when the filmstrip sits on the left
  or right.
