## 0.9.80

### Changes

- More comes along from a Lightroom Classic catalog: capture times you corrected there (a fixed
  camera clock, the date you gave a film scan), copyright, GPS position, city and country. They
  only fill what the picture itself does not say, and anything you set later in FerrumPix wins.
  For raw files the copyright and location are kept next to the picture, so they stay even
  without the catalog. DNG and TIFF files also use a capture time saved into the file itself.

- Capture times of pictures taken in another time zone are now shown in your own time zone,
  when the file says where it was taken. Pictures are still sorted by the time the camera showed.
  A switch in Settings, Catalog, shows the camera time instead.

- New looks in the filter area: Frost Sheen, Candy, Summer Dust, Moon Silver, Café, Ivory,
  Fairground, Lagoon, Asphalt, Runway, Wanderlust, Denim, Speakeasy, Charcoal, Autumn Leaves, Bold,
  Fireside, Midsummer and Deep Sea. Moon Silver and Ivory are black and white. Desert Light, Film
  Grain and Timeless got new settings.

- Fewer filters that look alike: Contrast is now part of a stronger Dramatic, which also darkens
  and calms the colors a little. The instant photo filter is gone, it looked almost like Warm, and
  so is Fade, which looked almost like Matte. Aged looks like a yellowed print now. Base Light,
  Color Harmony, Luminous Curve, Color Haze and Clear View are gone; their character lives on
  in Frost Sheen and Candy.

- The Filter tool can show its filters as small previews of your picture instead of plain
  buttons. The new button next to the reset arrow switches between the two. Each preview shows
  exactly what a click on it does, and a click applies the filter as before. The previews are only
  made while the Filter tool is open, so they never slow down other work.

- The reset arrow of the filter area now takes a look out completely. Before, it only cleared
  the name, and the look's light, color and curve settings stayed, so the picture hardly changed.
  Changes you made by hand after choosing the look stay as they are.

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
