## Unreleased

### Changes

- CTRL+SHIFT+C copies the adjustments of a picture and CTRL+SHIFT+V pastes them, in the gallery
  onto the whole selection and in the editor onto the open picture. Both were only in the menu
  before.

- Every search field has a small cross that empties it as soon as something is typed in it.

### Fixes

- With a search text left in the keyword list of the gallery, the list was empty and said that
  no keywords were assigned. It now says that nothing matches.

- In the Flatpak, a file the user may not write to could not be moved to the trash on a second
  drive, although the file manager could do it. FerrumPix now puts it into the trash of that
  drive itself, where the file manager finds and restores it.

