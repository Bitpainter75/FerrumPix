## Unreleased

### Changes

### Fixes

- JPEGs that carry their capture date only in the embedded XMP, as some photo managers on Windows
  write them, showed the file date as capture date under the thumbnail, so the date jumped
  whenever keywords were written into the file. The capture date now comes from the XMP. Pictures
  without the usual capture time also fall back to the digitized time before the file date.
  Sorting, grouping and search follow, and already catalogued pictures are read once more.

- Star ratings stored in a picture were not always shown: a rating given in percent was read as
  five stars, and a sidecar file without stars could hide the stars of the picture. A new sidecar
  file no longer gets an empty rating just because the catalogue has none yet.

