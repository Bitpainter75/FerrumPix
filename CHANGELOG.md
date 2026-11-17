## Unreleased

### Changes


## 0.9.86

### Changes

- Copying the adjustments of a picture and pasting them onto others now works the same in the
  gallery, the viewer and the editor. Before, both commands were only in the context menu of the
  gallery. The viewer has them in its menu for the picture shown, the editor in its menu and as
  two buttons below the saved adjustments. CTRL+SHIFT+C copies and CTRL+SHIFT+V pastes in all
  three.

- A RAW file can now carry several variants of its edit, for example one in color and one in
  black and white. The new Variants entry in the context menu of the gallery, the viewer and
  the editor creates a variant from the current edit or without any adjustments, next to the
  edit the file already has, which is called Standard. Each variant is a picture of its own,
  with its own rating, color label, pick mark and keywords: a tile in the gallery, a picture in
  the filmstrip, in fullscreen and in the comparison of the viewer, and a file of its own in an
  export. The window title names the variant on screen, and saving in the editor goes into the
  variant that is open. Deleting or renaming a variant leaves the file alone; when Standard is
  deleted, the next variant takes its place. All variants live in the one sidecar beside the
  file, so they move along when the file is renamed, moved or copied, and deleting the file
  says how many variants go with it.

- In the tool bar of the editor, the Adjust tool is now called Light and has a symbol of its
  own, and the Filters tool is called Adjustments. The saved adjustments have moved from Light
  to Adjustments, where the new Variants group lists the variants of the open picture, switches
  between them, and creates, renames and deletes them. The automatic enhancement stays in the
  Light panel. Both groups can be hidden and reordered in the settings like the others.

- The Lighting tool of the editor now sits below Mask, above Text.

- With the marks in the filmstrip switched on in the settings, the filmstrips now also show
  whether a picture is kept or rejected.

- The filter of the gallery is laid out in three columns and can now filter by camera, lens,
  focal length and ISO. It offers what the pictures on screen actually carry, several values of
  a group can be chosen at once, and the column is left out where no picture has shooting data.

- In the editor, the button that resets all edits has moved between Save as and Delete, away
  from Undo, where it was hit by mistake.

- Every dialog shows the symbol of its action in front of its heading, the same one the menu
  uses, and so do the groups in the export dialog.

- Every search field has a small cross that empties it as soon as something is typed in it.

- The filter of the gallery can now narrow the pictures down to a period of the shooting date,
  from a day, up to a day, or both. It sits below the color labels.

- The mark for an edited picture in the filmstrips now sits in the lower right corner and looks
  like the marks on the left.

### Fixes

- Opening the display profiles in the settings could close FerrumPix.

- After choosing a language or a date format in the settings or in the setup assistant, the
  selection box was empty.

- The opened date picker did not follow the theme, and its placeholders for day, month and year
  were not translated.

- A RAW file edited and saved in the editor kept the preview of the camera as its tile in the
  gallery and the filmstrips, unless developed previews were switched on in the settings. The
  tile of an edited RAW file now always shows the edit.

- With a search text left in the keyword list of the gallery, the list was empty and said that
  no keywords were assigned. It now says that nothing matches.

- In the Flatpak, a file the user may not write to could not be moved to the trash on a second
  drive, although the file manager could do it. FerrumPix now puts it into the trash of that
  drive itself, where the file manager finds and restores it.

