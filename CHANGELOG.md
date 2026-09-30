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

- The new "Merge into background" in the layer's context menu bakes the selected layers, or a
  whole group, into the photo.

- Image and paint layers can be trimmed from the layer's context menu: the fully transparent
  border is cut away, and the picture on the layer stays exactly where it was.

- A new version is now pointed out right next to the logo in the title bar, not only in the
  settings. FerrumPix looks once at startup.

- The image comparison develops RAWs at half resolution, which makes stepping through them many
  times faster. For judging sharpness up close, full resolution can be switched back on in the
  settings.

- RAW files that carry no usable preview of their own, such as DNGs written by CHDK, now show up
  in the viewer and the gallery more than ten times faster.

### Fixes

- Merging or rasterizing layers while one of the adjustment tools was open could leave the
  layers untouched.
