## Unreleased

### Changes

- Copying the adjustments of a picture and pasting them onto others now works the same in the
  gallery, the viewer and the editor. Before, both commands were only in the context menu of the
  gallery. The viewer has them in its menu for the picture shown, the editor in its menu and as
  two buttons below the saved adjustments. CTRL+SHIFT+C copies and CTRL+SHIFT+V pastes in all
  three.

- Every search field has a small cross that empties it as soon as something is typed in it.

### Fixes

- With a search text left in the keyword list of the gallery, the list was empty and said that
  no keywords were assigned. It now says that nothing matches.

- In the Flatpak, a file the user may not write to could not be moved to the trash on a second
  drive, although the file manager could do it. FerrumPix now puts it into the trash of that
  drive itself, where the file manager finds and restores it.

