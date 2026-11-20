## Unreleased

### Changes

- The search field of the gallery and the text of a saved search understand + and -: +word must
  appear, -word must not. +Holiday +Beach -Rain finds pictures with both keywords and without
  rain. Several words without a sign still find pictures with any of them. The search dialog and
  the search field explain this.

- Copying and pasting in the editor shows the busy indicator on the picture while the full-size
  copy is being prepared, and keeps it until the pasted layer is there. Copying a large photo no
  longer freezes the window for seconds on Linux when a clipboard history is running.

- Film negatives are inverted from the light that actually passed through the film. A negative
  photographed with a camera is developed without the camera's tone curve for this, and scans
  saved as JPEG or TIFF are inverted on linear values as well. Colours in dense areas such as a
  sky stay true, and the grain there is no longer exaggerated. The positive comes out a little
  brighter and softer than before. Edits saved with an earlier version keep their look; switching
  the negative off and on again uses the new way.

### Fixes

- In the gallery search field, several words now work as in the catalog search, and file patterns
  like *.jpg work there too. Before, the whole text was looked for as one piece.

- AVIF pictures open again on every platform: the gallery shows their thumbnails, and the viewer
  and the editor show the picture. Before, they stayed empty.

- HEIC and AVIF pictures with 10 or 12 bit keep their fine tonal steps and can be saved with
  16 bit. Very saturated colours from phones in Display P3 are brought into range gently instead
  of being cut off, so bright reds and greens no longer turn into flat areas. Pictures with 8 bit
  in sRGB look exactly as before.

- The search field in the settings was too narrow for its hint, which was cut off. It now just
  says Search; the shortcut CTRL+F shows when you point at the field.

- The title of the dialog for renaming several pictures at once stayed in German in every other
  language.

- RAW files from the Panasonic DC-GH5S use the right white level. Bright highlights were rendered
  too dark and blown-out areas never turned fully white.

