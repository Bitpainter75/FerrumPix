## 0.9.89

### Changes

- Text objects get a line spacing setting in the text panel, from 50 to 300 percent of the font
  size; Automatic keeps the spacing of the font as before. It also applies to text watermarks.
  Multi-line text from PSD files with one line spacing for the whole text now stays editable text
  instead of becoming a picture, and exported PSD text layers keep the spacing.

- Opening a PSD file or a FerrumPix project (.fpx) in the editor now tells you which fonts of its
  text are missing on this computer and how many text layers use each, also inside groups. The
  text stays editable text, set in the replacement font that is named in the message.

- JPEG 2000 files (JP2, J2K, HTJ2K) now open in the gallery, viewer and editor, with their full
  tonal depth for 16-bit exports. Save keeps the edit beside the file.

- OpenEXR, Radiance HDR and TIFF files with floating point values now open and are developed like
  a RAW: light brighter than white is kept, and Exposure and Highlights bring it back into the
  picture. The editor offers the RAW adjustments for them, Save keeps the edit beside the file.

- Sigma X3F files now show in the gallery, the viewer and the editor, with camera and shot data,
  and are developed from the sensor data like other RAW files, with the RAW controls in the
  editor. Edits are kept beside the picture, as for RAW.

- More picture endings show in the gallery and open in the viewer and the editor: JPEG saved as
  .jfif or .jpe, as browsers on Windows often do, stereo JPEG (.jps) as the side by side pair,
  animated PNG (.apng, first frame), WBMP and Windows cursors (.cur). Their edits are kept beside
  the picture.

- The previews inside HEIF files from Fujifilm cameras now load, and the gallery placeholders of
  rotated HEIF pictures are upright like the picture.

- Ratings, pick marks, colour labels and keywords from RapidRAW's sidecar files (.rrdata) now show
  in the gallery and can be searched once "Read data from RawTherapee, ART, RapidRAW and Lightroom"
  is switched on in the catalog settings. FerrumPix only reads these files and never writes into
  them; what you set in FerrumPix or in an XMP sidecar comes first. When you rename, move, copy or
  delete a picture in FerrumPix, its .rrdata files go along with it, virtual copies included.

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

- Colour negatives keep greys neutral from the shadows to the highlights. The red and blue layers
  of a film bend differently, so a grey used to turn slightly coloured between black and white;
  the inversion now finds the grey areas of the picture and evens out red and blue there, green
  keeps the tones. A picture without grey areas is inverted as before. Edits saved with an
  earlier version keep their look.

- When the film negative inversion is switched on for a picture that looks like a positive, such
  as a slide scan or an ordinary photo, the editor says so below the checkbox. Pasting or applying
  adjustments with the inversion to several pictures names the ones that look like a positive.
  Colour negatives with their orange base and black and white negatives get no such note.

- Loading the shape of a layer or the mask of a selection or mask layer as a selection can now
  add to the current selection, subtract from it or keep only the overlap, not just replace it.
  In the layers panel, CTRL+click on the thumbnail replaces, CTRL+SHIFT adds, CTRL+ALT subtracts
  and CTRL+SHIFT+ALT intersects; the context menu of a layer offers the same as "Load as
  selection". Each load is one step in the history and can be undone.

- The colour picker and the colour mixer of objects now show red, green, blue and alpha as numbers
  from 0 to 255 that can be typed in, next to the hex value and the opacity. Alpha and the opacity
  slider always move together. Colours that have no opacity, such as the light colour and Replace
  colour, no longer show an opacity slider or an alpha field.

- Every eyedropper in the editor now shows the colour under its tip next to the pointer, also
  the colour range of masks and selections, and it shows the colour that a click would take. A
  new editor setting chooses what it says: the hex value, R G B, or both, which is the default.
  Where the eyedropper takes transparency along and the spot is not fully opaque, the value
  includes alpha.

- PSD files with 32 bits per channel or in Lab colour now show their full picture in the
  gallery, the viewer and the editor instead of the small, blurry preview stored inside. When
  opening a 32-bit file whose layers cannot all be taken over, you can now choose to open the full
  picture instead.

- Lowering Highlights on JPEG, TIFF, PNG, HEIC and other pictures that are not RAW no longer
  strengthens bright halos along hard edges, and the detail in bright areas stays closer to the
  original there. RAW files look as before.

### Fixes

- Text layers in PSD files that a text object cannot reproduce faithfully, such as text in a
  text box, vertical text, or text that mixes fonts, sizes, colours or styles, now come in as a
  picture exactly as stored in the file, and the notice when opening the file says so. Before,
  they came in as editable text that looked different. Simple text layers now also sit at the
  right height.

- Scrolling the group view of the gallery is smooth again. Tiles that scrolled into view were
  often built from scratch instead of being reused, which made scrolling stutter.

- Scrolling the gallery grid and group view is much lighter on the graphics card. The stars,
  heart, metadata signs and buttons on the tiles now share a ready-made shadow instead of
  computing it for every tile, which made scrolling stutter on many computers. The shadow is a
  little darker, and the buttons for viewing, adjusting and deleting sit on a darker background,
  so they stand out better on bright pictures.

- Preloading the thumbnails of a large folder no longer pushes out thumbnails that were just
  loaded. In folders with more pictures than the thumbnail memory holds, the gallery kept loading
  and throwing away thumbnails, which slowed down the visible tiles while scrolling and drove up
  memory use.

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

- Removing noise with the model no longer leaves coloured patches and squares in dark areas next
  to bright ones, as in night shots. A dark sky beside a lit building used to turn purple or green
  in sharp-edged blocks.

- Open with: when the chosen program is installed as a Snap and the picture is on a memory card,
  a USB drive or elsewhere outside your home folder, the status line now says that the program
  needs permission to read it, and how to give it. Before, the program started and quietly failed
  to open the file.

- Some damaged RAW files, above all Canon CR3, could crash the application while being developed.
  They are now rejected or developed as far as they can be read.

- Rotating a picture from Immich or Nextcloud in the viewer no longer offers to save the rotation.
  It only went into the temporary copy on this computer and was lost later; now it stays a view,
  and the picture on the server is unchanged.

- Right-clicking a layer in the editor no longer ends a slider drag that is still in progress and
  no longer adds anything to the history unless you actually choose "Load as selection".
