## 0.9.72

### Changes

- **Stronger luminance noise reduction with a working Detail slider:** the luminance noise
  reduction now smooths as strongly as other photo programs at the same value; before, even 100
  did less than they do at 25. The Detail slider now keeps fine texture such as hair and fabric
  while flat areas stay smooth, and it has a visible effect from about the middle of its range.
  Presets with a luminance value now look as intended. Pictures you already edited with luminance
  noise reduction come out smoother.

- **Dragging in the viewer moves the picture:** when you are zoomed in, dragging with the left
  mouse button now scrolls the picture instead of drawing a crop frame. If you liked the crop
  frame, turn on "Crop by dragging" in the viewer settings.

- **Faster colour noise reduction:** with colour noise reduction on, moving exposure, curves or
  colour sliders no longer redoes the noise reduction each time, and the noise reduction itself
  runs faster and on the graphics card when that is switched on.

- **Next picture after rating:** a new viewer setting moves on to the next picture as soon as you
  give it stars, so you can rate a whole folder with the number keys alone.

- **Colour mixer and curves now acts before the curves:** in presets that use both, the
  colour mixer now acts before the curves, so the colour look the curves add stays as intended.
  The colour mixer also picks its colour ranges more precisely, so strong colours such as stage
  light react to the right slider, and lowering saturation also reaches muted colours.

- **Shuffle the slideshow:** a new viewer setting plays the slideshow in random order. Every
  picture is shown once before the order is mixed again.

- **Crisper sharpening:** the Sharpening slider now brings out fine texture such as hair and
  fabric, without sharpening pixel noise more. Pictures you already sharpened come out a little crisper.

### Fixes

- **Clearer graphics card check:** after a successful check, the performance settings now say
  plainly that image editing uses the graphics card. On a Mac with an Apple chip, the card shows
  up with the chip's name as a built-in graphics unit instead of a number.

- **No more jagged edges around coloured stage light with presets:** presets that tint the
  shadows, such as a teal look, could leave a hard, jagged edge and a green fringe where red stage
  light fades into the dark. The light now fades out smoothly.

- **Presets only once in the list:** importing the same XMP preset or LUT again, even from
  another folder or under another name, no longer adds a second tile. Duplicates already in the
  list are removed once.

- **Camera, date and location for Huawei HEIC photos:** HEIC pictures from Huawei phones now show
  their camera, capture date and location in the info panel.

- **Loading a folder of presets or LUTs:** a large folder no longer freezes the window. A progress
  bar shows while it is read.

- **Canon EOS R8 Mark II colours:** its RAW files were developed as if shot in daylight, because
  the camera's white balance was not read. They now use the white balance set in the camera.

- **Canon EOS R6 brightness:** with the brightness matched to the camera model, its RAW files
  were developed brighter than other cameras. The R6 now gets its own measured value like every
  other model, so unedited pictures start a little darker.

- **Soft transitions in coloured stage light:** where strongly coloured light blew out skin or
  clothing, the bright area had a hard edge against the coloured light around it and looked
  washed out orange. RAW files are now developed with more room for strong colours, so the light
  fades smoothly into the bright area.

- **More natural strong colours:** RAW files keep strong colours such as stage light, flowers
  and saturated clothing intact further into the development, so they come out closer to what
  you saw, with smoother gradations.

- **Converted DNG files no longer too bright:** a DNG made from another RAW file with a converter
  or another photo program often started brighter than the original RAW. It now develops like
  the original.

