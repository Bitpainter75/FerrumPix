## Unreleased

### Changes

- **Dehaze works more like you would expect:** removing haze clears hazy pictures more strongly
  and gives them deeper blacks, while clear shadows stay protected. Adding haze now lays a veil
  that follows the picture, stronger in soft, bright areas and lighter on fine detail, instead of
  a flat grey wash.

- **Shadows and Highlights adapt better to each picture:** both sliders now look at the whole
  brightness distribution of the picture, not just its average brightness, and suit dark and
  bright pictures better. Used together, they now hold each other back a little, so lowering
  Highlights and lifting Shadows at the same time no longer overdoes it, for example on skin in
  dark stage photos. Lifted shadows also gain some colour instead of turning grey, and darkened
  shadows lose a little. Lowering Highlights no longer makes darkened midtones more colourful.

- **Tone curves keep colours more natural:** the main tone curve, including the parametric
  curve of presets, now changes brightness and contrast without twisting colours as much. Skin
  tones in particular stay warmer when a preset darkens the shadows. 

- **Similar pictures:** a new view in the gallery groups near duplicates of a folder, such as the
  shots of a series, with a sharpness bar on each picture and the sharpest one marked. Three levels
  decide how close the pictures have to be.

- **Keywords inside the file:** two new settings write the keywords you add into JPEG, PNG and WebP
  files themselves, as XMP, IPTC or both, when you add them and when you save or export. AI
  keywords are never written into files.

### Fixes

- The thumbnail memory cache now holds at least 1000 thumbnails. With a smaller cache, tiles in
  large folders could stay dark.

- HEIC photos from Huawei phones that were already in the gallery before 0.9.72 now show the
  camera under their tile too, not only in the info panel.
  
- Deleting inside an Immich or Nextcloud album now only removes the pictures from that album.
  They stay in your library on the server. Before, the picture was deleted from the whole library.
  The menu entry says "Remove from album", and it works even if deleting on the server is turned
  off in the settings.
  
- Selected text in input fields stays readable with a light accent colour such as yellow, for
  example the file name in Save as.
  
- The Save as dialog is wider, so the Carry over buttons and the destination folder fit again,
  and the line above the file name is no longer cut off with a larger font size.

