## 0.9.67

### Fixes

- After undo, redo or the eye of a group, the sliders of Details, Noise and some of Sharpness
  stayed where they were, although the picture already showed the restored values. They follow
  now.

- The reset button and the eye of an adjustment group only touch that group's own sliders. Details
  also reset the noise sliders, and the reset button of Vignette reset the grain and the lens
  corrections as well.

## 0.9.66

### What's new

- **RAW development on macOS without Homebrew.** The Mac packages now bring their own LibRaw, so
  RAW files are developed from the sensor data even if nothing is installed. A LibRaw from
  Homebrew is still preferred when it is there.

- **Compare a single curve.** Right-click RGB, R, G, B or L above the tone curve to set that
  channel's curve back to a straight line for a moment; the next right-click brings it back,
  just like comparing a single slider.

- **Compare a whole group.** The eye next to the reset button of Light, Color, Tone curve,
  Color mixer, Colour grading, Calibration, Channel Mixer, Gradient Map, Posterize, Threshold,
  Sharpness, Blur, Noise, Details, Vignette and Grain hides that group's adjustments for a
  moment, so you see the photo without them; click it again to bring them back.

### Fixes

- Moving the temperature of a RAW photo no longer throws away the tint of the shot. Even a small
  nudge could tip the whole picture towards green or magenta, most visibly on phone photos; the
  same happened with automatic enhancement, the white balance picker and presets that shift the
  white balance. The tint row now shows the tint of the shot, just as the temperature row shows its
  temperature, and a double-click brings both back.

- The tooltip of the button that leads from a burst back to its folder is translated now.

- Enlarging the canvas no longer makes the photo shrink inside the editor. Even one extra pixel
  could show the picture at half its size with a huge border around it; the saved image was
  always right, now the editor shows the same.

- Scans saved as TIFF or DNG are read far less often: reading their details no longer loads the
  whole file, so indexing watched folders, opening a picture in the editor and the info panel are
  much quicker on a network drive. A film scan as DNG used to travel over the network up to four
  times, now about once.

- The Microsoft Store version no longer looks for a newer version on GitHub; updates come through
  the Store.


