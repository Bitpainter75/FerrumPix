## 0.9.67

### What's new

- **Point color.** In the color mixer, a second view picks a color in the photo with the
  eyedropper and shifts only that color in hue, saturation and luminance, with a range for how
  far around it other colors are affected. Up to eight colors, shown as circles; "Show range"
  greys out everything a color does not reach. Presets with point color now arrive complete.

- **Colored sliders.** A new setting shows sliders with a color meaning as a color gradient,
  such as temperature, tint, the color mixer, calibration, the channel mixer and point color.
  Off by default.

- **The eye of a group works on layers too.** It hides the group's adjustments of the selected
  layer or correction layer for a moment and brings them back.

- **Tilt-shift.** A new group in Effects keeps a band through the picture sharp and blurs more
  and more towards the edges, like a tilted lens. Width, transition, angle and position of the
  band can be set, and the group can be hidden or moved in the settings like any other.

- **Depth fog.** Another new group in Effects lays fog over the picture by distance: near things
  stay clear, far things fade into the fog colour. Like depth blur it needs the depth model and is
  applied into the pixels with its own button.

### Fixes

- The reset button of a group works on a selected layer right away instead of only after the
  layer is deselected.

- The eyedropper works on a selected layer: the click no longer starts moving the layer, and the
  point color takes the color of the layer instead of the photo below it.

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


