## 0.9.57

### What's new

- A single layer, or several at once, can now go to G'MIC from the layer's context menu. Text,
  shapes and the layer's own adjustments are rasterized on the way, and the result comes back as
  a new layer right above. The editor's footer and stage menus still send the whole picture.

- CTRL+E merges the selected layer with the visible layer below it; the bottom layer is merged
  into the photo. The eraser in the drawing tool stays on E.

- Image and paint layers can be trimmed from the layer's context menu: the fully transparent
  border is cut away, and the picture on the layer stays exactly where it was.

### Fixes

- Merging or rasterizing layers while one of the adjustment tools was open could leave the
  layers untouched.
