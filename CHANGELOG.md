## 0.9.57

### What's new

- A single layer, or several at once, can now go to G'MIC from the layer's context menu. Text,
  shapes and the layer's own adjustments are rasterized on the way, and the result comes back as
  a new layer right above. The editor's footer and stage menus still send the whole picture.

- CTRL+E merges the selected layer with the visible layer below it. The eraser in the drawing
  tool stays on E.

- Merged layers are trimmed to their content right away.

- Rasterizing a layer now turns it into an image layer in the same place instead of merging it
  into the photo, trimmed to its content right away. A layer that already is an image no longer
  offers the option.

- The new "Flatten image" in the layer's context menu bakes the selected layers, or a
  whole group, into the photo.

- Image and paint layers can be trimmed from the layer's context menu: the fully transparent
  border is cut away, and the picture on the layer stays exactly where it was.

- A new version is now pointed out right next to the logo in the title bar, not only in the
  settings. FerrumPix looks once at startup.

- The image comparison, the quick preview and the viewer (when it develops RAWs itself) now do
  so at half resolution, which makes stepping through them many times faster. For judging
  sharpness up close, full resolution can be switched back on in the settings, separately for
  the viewer and the comparison.

- The brush panel has an "Add paint layer" button. The new layer is selected right away, so
  you can start painting on it.

- The watermark tool offers "Insert image" and "Insert text" side by side at the top of its
  panel. Both place a watermark right away, or switch the selected one over.

- RAW files that carry no usable preview of their own, such as DNGs written by CHDK, now show up
  in the viewer and the gallery more than ten times faster.

- Every color field now opens the same color wheel as the drawing and object tools, with
  opacity, hex value and the recently used colors. The previous color stays next to the new one
  and brings it back with a click.

### Fixes

- Merging or rasterizing layers while one of the adjustment tools was open could leave the
  layers untouched.

- Stars, favorite, color label and keywords set in the editor or the viewer now show up on the
  gallery thumbnails right away when you go back, and names given to people show up in the
  gallery's info panel. They were saved all along, but the gallery only showed them after
  reloading the folder.

- The favorite heart now has the same shadow as the rating stars, so it no longer gets lost on
  bright parts of a thumbnail.

- Text and number fields in drop-down panels no longer show a lighter ring around the text, and
  their highlighted frame keeps its rounded corners.
