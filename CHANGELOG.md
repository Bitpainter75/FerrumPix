## 0.9.72

### Changes

- **Fewer sliders under Grain:** the sliders that added noise, colour noise and dust and
  scratches are gone from the Effects panel. Pictures that already use them keep their look, and
  resetting the Grain group clears them.

- **Stronger luminance noise reduction with a working Detail slider:** the luminance noise
  reduction now smooths as strongly as other photo programs at the same value; before, even 100
  did less than they do at 25. The Detail slider now keeps fine texture such as hair and fabric
  while flat areas stay smooth, and it has a visible effect from about the middle of its range.
  Presets with a luminance value now look as intended. Pictures you already edited with luminance
  noise reduction come out smoother.

- **Dragging in the viewer moves the picture:** when you are zoomed in, dragging with the left
  mouse button now scrolls the picture instead of drawing a crop frame. Cropping is done in the
  editor.

### Fixes

- **Presets only once in the list:** importing the same XMP preset or LUT again, even from
  another folder or under another name, no longer adds a second tile. Duplicates already in the
  list are removed once.

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

- **Exposure slider without a jump in strong colours:** while you drag the exposure slider on a
  RAW, strongly coloured areas now look almost the same as after the picture has been rebuilt.

- **Converted DNG files no longer too bright:** a DNG made from another RAW file with a converter
  or another photo program often started brighter than the original RAW. It now develops like
  the original.

- **Smoother sliders:** dragging a slider in the editor stuttered again in 0.9.71. It runs
  smoothly again.

- **Colour noise reduction off by default again:** a RAW without edits now starts with colour
  noise reduction at 0, because reds looked too strong with bold presets. If you set your own
  value in Settings, it stays.

