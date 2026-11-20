## Unreleased

### Changes

- New setting in the editor settings: Save JPEG, PNG and WebP edits beside the picture. When on,
  Save keeps the original untouched and stores the edit in a sidecar file next to it, as for RAW.
  It is off by default, so Save writes into the picture file as before. TIFF, HEIC, AVIF,
  JPEG XL, BMP and GIF now always save their edit beside the picture, so Save works for them
  instead of only Save as.

- An edit stored beside a JPEG, PNG, TIFF, HEIC or PSD now shows in the gallery tile, the
  viewer, the comparison, prints, collages and exports, and can be copied to other pictures.
  Pasting adjustments, applying adjustments with overwrite and rotating in the viewer add to that
  edit instead of rewriting the picture. When such a picture is later saved into its file, or
  resized or watermarked with overwrite, the edit goes into the pixels once and the sidecar file
  is removed.

- When an edit does not fit into the sidecar file (brush, retouch, layers, objects), Save asks
  whether to keep it as a project next to the picture or to use Save as, instead of being greyed
  out. This also applies to RAW and PSD; after saving as a project, the editor continues in it.

- The keyline frame has a new slider for the position of its fine line. At 0 the line sits
  against the picture as before, at 100 on the outer edge of the frame, and it follows rounded
  corners.

- The frame remembers its last settings: a new frame on the next picture, also after a restart,
  starts with the same kind, colours, gradient, widths, spacings and shadow. The reset button of
  the frame group goes back to the original settings.

- Copying and pasting adjustments and saved adjustments now take the frame along. Pasting or
  applying replaces a frame the picture already has; without a frame in the copy, the picture
  keeps its own. Text, shapes and other objects stay with their picture. To leave the frame out,
  switch it off in the frame group before copying or saving. A RAW or PSD with a frame can still
  be saved into its sidecar file.

- The footer of the viewer has a button for focus peaking, next to the one for the mouse wheel.
  It does the same as the key K.

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

- Scrolling the group view of the gallery is smooth again. Tiles that scrolled into view were
  often built from scratch instead of being reused, which made scrolling stutter.

- The tile ".." that leads up to the parent folder no longer shows a date in the gallery. Its date
  stood among the pictures as if it belonged to them. Sorting by date keeps it at the front.

- The hint under the 16 bit button now says clearly that 16 bit can be saved as TIFF, PNG or
  JPEG XL, as long as the picture comes from a RAW file or from a TIFF, HEIC or AVIF file with
  more than 8 bit. Before, it read as if only TIFF could hold 16 bit.

- The before and after slider in the editor stays on screen when you zoom in or move the picture.
  If it would leave the visible part, it stops at the nearest edge, and its round handle sits in
  the middle of what you see instead of the middle of the whole picture.

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

- The before side of the before and after comparison in the editor shows the same frame as the
  edited picture for RAW files shot in a different aspect ratio, such as 65:24 or 16:9 on a
  Fujifilm camera. Before, the whole sensor area was squeezed into the cropped frame.

- Pentax RAW files shot with highlight correction come out as bright as intended. The camera
  records them a stop darker to protect the highlights, and they used to look a stop too dark;
  this now shows as a starting value of the exposure slider. Pentax models whose base brightness
  was measured on such pictures, among them the K-70, KF and KP, no longer develop other pictures
  a stop too bright.


- The ICC badge in the gallery no longer names the platform an embedded colour profile is meant
  for, which read as if Apple or Microsoft had made it. It shows who created the profile instead.
