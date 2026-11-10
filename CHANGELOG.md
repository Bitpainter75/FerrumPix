## Unreleased

### Changes

- More comes along from a Lightroom Classic catalog: capture times you corrected there (a fixed
  camera clock, the date you gave a film scan), copyright, GPS position, city and country. They
  only fill what the picture itself does not say, and anything you set later in FerrumPix wins.
  For raw files the copyright and location are kept next to the picture, so they stay even
  without the catalog. DNG and TIFF files also use a capture time saved into the file itself.

- Capture times of pictures taken in another time zone are now shown in your own time zone,
  when the file says where it was taken. Pictures are still sorted by the time the camera showed.
  A switch in Settings, Catalog, shows the camera time instead.

- **History brush**: a new kind in the image brush paints the original picture back, without the
  retouching, brush strokes and adjustments on that spot. The strokes collect in a History layer,
  so they can be hidden, weakened or deleted later, and they are saved with your edit.

- Save as, Export and the other batch functions offer a choice of target folders and file names,
  like the import: the current and the last used folder, plus your own folder and name templates
  from Settings, Editor. Templates can use placeholders such as `{folder}/export`, which puts the
  result into a subfolder next to each picture, even when the pictures come from several folders.

- The editor can save automatically when you move on to another picture or leave it, instead of
  asking (Settings, Editor; off by default). Raw files keep their edits next to the picture,
  JPEG, PNG and WebP are overwritten. Where a picture can only be saved under a new name, it still
  asks.

