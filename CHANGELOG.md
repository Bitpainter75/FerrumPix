## Unreleased

### Fixes

- Lens correction is now only offered where it works. It corrects the sensor data while a RAW file
  is developed, so it never had any effect on a JPEG, TIFF or HEIC photo, even though the group
  showed the detected lens and let you move all three sliders. Those photos usually come out of the
  camera or a RAW developer with the correction already applied, and doing it a second time would
  bend the picture the other way.

- The strength sliders for distortion, colour fringes and corner darkening can only be moved when
  the lens has measurements for that correction. Before, only the tick box above them was locked.

- Choosing a lens by hand now reaches the picture. If the capture data named no lens, the panel
  showed your choice but the photo stayed as it was, while the thumbnail and the exported file were
  corrected.

- Painting with the healing brush, clone stamp or blur brush stays smooth on large pictures.
  Before, the editor became sluggish from the second stroke on.

- A clone stamp stroke on a large picture is applied many times faster after you let go.

- Pictures pasted into the gallery get their preview and their capture data, and are added to the
  catalog. Before, a pasted picture could stay without a preview.

- TIFF photos now show in the viewer, in full screen and in the quick preview you get with the
  SPACE key. They appeared in the gallery and opened in the editor, but stayed empty everywhere
  else. The quick preview also shows SVG and icon files now.

- Reducing noise with a model, and upscaling, now work on every picture. On macOS they failed on
  every photo, and elsewhere on pictures with object layers.

## FerrumPix 0.9.46

### What's new

- **JPEG XL, reading and saving.** Photos in JPEG XL show up in the gallery and open in the
  viewer and the editor, with their colour profile, rotation and capture data. You can save as
  JPEG XL wherever you can choose a format, with the same quality setting as JPEG; 100 means
  lossless. When converting JPEG files to JPEG XL, they can be repacked without any loss: about a
  fifth smaller, and the original JPEG can be restored exactly. The Windows downloads bring
  everything along, on Linux the packages recommend libjxl.

- **Clarity that works like clarity.** The slider now lifts contrast in the midtones across a wide
  area, without light or dark rims along strong edges, and brings up far less noise. Before, it
  acted like a second sharpening. Edits with clarity look somewhat different than before.

- **Paste pictures straight from the clipboard.** A screenshot or an image copied in the browser
  becomes a new layer in the editor, not only image files. What you copy in the editor now also
  goes to the clipboard as a picture, so it can still be pasted later and in other programs.

- **Black and white point on the tone curve.** The end points of the curve can now be dragged
  inward as well, on every channel, so you can set the black and white point or tone colours per
  channel.

### Fixes

- Projects with several masked adjustments open faster, most of all when the masks cover only part
  of a straightened or rotated picture.

- Selecting a mask layer on a straightened, rotated or cropped picture shows its red overlay
  almost at once instead of after a second or more.

- The depth blur is calculated about three times faster, and the healing brush repairs several
  times faster, most of all with large brush sizes.

- The depth blur with an aperture of three or six blades no longer fails, and with four or five
  blades the picture edges are no longer smeared wrongly.

- A cropped or straightened project opens in the editor as it was saved, instead of showing the
  whole uncropped picture for a moment first.

- A layer mask now follows its layer while you drag, scale or rotate it, not only after you let
  go.

- Erasing on an image layer removes the pixels for good. Before, the erased spot flashed back
  while moving the layer and stayed behind as a hole when the layer was moved elsewhere.

- Copying a selection taken from a layer copies only that layer. Transparent and see-through parts
  stay transparent instead of showing the photo underneath.

- Upscaling in the editor, and applying saved edits that need a model, show the progress display
  with its cancel button. The picture stays locked while they run, so no change made in the
  meantime is lost.

- HEIF photos with the endings .heif and .hif, as some Fujifilm and Canon cameras save them, now
  appear in the gallery.

- Smaller things: the channel switcher of the tone curve also marks a channel whose end point was
  moved, lens correction knows a few more cameras and lenses, and the bundled HEIC decoder on
  Windows is up to date.
