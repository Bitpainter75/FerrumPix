## 0.9.68

### What's new

- **Highlights and shadows keep their detail.** Both sliders now work on the brightness of the
  surroundings instead of each single pixel: pulling the highlights down brings out the
  structure in a bright sky instead of flattening it, and lifting the shadows does the same in
  dark corners. Edits you saved before keep their look.

- **Whites adapt to the picture.** The whites slider now looks at how close the brightest parts
  already are to white. A flat picture opens up further, one whose highlights already reach
  white is lifted more gently, so the slider behaves much more like you would expect from a raw
  editor. Highlights, shadows and whites also read a film negative as the positive you see.

- **Depth fog reaches the foreground.** A new Foreground slider in Depth fog puts some fog over
  the nearest parts of the picture too, so a person or an animal close to the camera no longer
  stays perfectly clear while everything behind them disappears in the mist.

- **File names in the photo wall.** The photo wall has no caption strip under its tiles, so a raw
  file and its .fpx project looked the same. Hovering over a tile now shows its name with the
  extension and the details you would otherwise see under it, in the same info box the EXIF and
  XMP badges use.

- **The eyedropper shows what it hits.** A small label follows the eyedropper over the picture
  with a swatch and the value of the color under its tip.

- **File name placeholders with the time.** Batch rename has two more placeholders to click
  that insert the file date and the date taken together with the time of day. The file name
  field of the other batch dialogs now offers the same placeholders to click.

- **Batch rename resolves name conflicts.** If two pictures would get the same name, or the
  name is already taken in the folder, the next one gets _1, _2 and so on instead of stopping
  the rename.

- **File extensions in lower case.** Saving under a new name, exporting, batch converting,
  renaming and batch renaming now write the file extension in lower case, so IMG_0001.JPG
  becomes IMG_0001.jpg. Files saved in place keep their name.

### Fixes

- Context menus and footer menus always open at the size of their content and from the top:
  no more changing sizes, missing rows or scroll arrows when the menu fits on the screen.
  The folder tree menu no longer shows doubled or trailing separator lines, and the dialogs for
  setting the capture date and the place are wide enough for their options.

- Picking exactly black, white, red or another standard color in a color picker no longer
  falls back to a different color. This affected the background of a new document, of a
  collage and behind transparent areas, the replace color brush and the gradient map. Edits
  and settings saved earlier with such a color show it correctly again.

- A rating, label or keyword on a RAW or PSD no longer replaces an edit file that could not be
  read, for example one from a newer version or on a drive that is offline; the same goes for
  rotating in the viewer and applying adjustments to a selection. The change stays in the
  catalog and the gallery says so.

- Overwriting a file or folder while pasting or moving keeps the old one until the new one is
  complete. If the copy fails, for example because a network drive drops out, the old file or
  folder is back as it was. Replacing a folder no longer takes along the sidecar of a photo
  that happens to have the same name.

- Saving a JPEG, PNG or WebP with its photo information no longer reports success when that
  information could not be carried over. The file stays as it was and the save is shown as
  failed. This now also covers rotating in the viewer, batch changes and replacing a picture on
  a server, even when the drive drops out in the middle of reading.

- Converting RAW files to DNG over an existing DNG no longer leaves a half written file behind
  when the conversion fails or is cancelled. The existing DNG stays as it was.

- Pasting, moving and converting to DNG no longer fail for files with very long names.

- An adjustment stacked above an object now updates right away when the object changes
  elsewhere in the picture, instead of only after the next full refresh.

- Saved searches, favorites and settings that could not be written are now reported in the
  gallery instead of silently counting as saved. A removed saved search no longer comes back
  after a restart, and a favorite that could not be stored is no longer reported as already
  present.

- XMP sidecar files from other programs now travel along when a picture is renamed, copied,
  moved or deleted, so ratings, keywords and edits are found at the new place. A shared
  photo.xmp stays where another file with the same name still needs it.

- When a sidecar cannot be moved to the trash or deleted, the remaining file is reported.
  Renaming a picture whose sidecar stays behind no longer says the rename failed: the picture
  is renamed, and the sidecar is named in the warning bar. The same warning no longer appears
  twice when a batch runs again over the same files.

- Objects with their own highlights, shadows or whites in a saved .fpx or edit file look the
  same as when they were saved. A thin object, or one that reaches over the edge of the
  picture, now looks the same while editing and after saving.

- Moving a folder to the trash no longer takes along the sidecar of a photo next to it that
  has the same name as the folder.

- Some NEF files from the Nikon Z50II and Z5II that could not be decoded showed colored noise
  and a green area. They now show the preview embedded in the file instead.
