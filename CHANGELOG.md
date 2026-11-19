## Unreleased

### Changes

- The search field of the gallery and the text of a saved search understand + and -: +word must
  appear, -word must not. +Holiday +Beach -Rain finds pictures with both keywords and without
  rain. Several words without a sign still find pictures with any of them. The search dialog and
  the search field explain this.

- Copying and pasting in the editor shows the busy indicator on the picture while the full-size
  copy is being prepared, and keeps it until the pasted layer is there. Copying a large photo no
  longer freezes the window for seconds on Linux when a clipboard history is running.

### Fixes

- In the gallery search field, several words now work as in the catalog search, and file patterns
  like *.jpg work there too. Before, the whole text was looked for as one piece.

- AVIF pictures open again on every platform: the gallery shows their thumbnails, and the viewer
  and the editor show the picture. Before, they stayed empty.

- The search field in the settings was too narrow for its hint, which was cut off. It now just
  says Search; the shortcut CTRL+F shows when you point at the field.

