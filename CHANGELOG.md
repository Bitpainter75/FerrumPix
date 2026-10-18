## 0.9.68

### What's new

- **Highlights and shadows keep their detail.** Both sliders now work on the brightness of the
  surroundings instead of each single pixel: pulling the highlights down brings out the
  structure in a bright sky instead of flattening it, and lifting the shadows does the same in
  dark corners. Edits you saved before keep their look.

- **The eyedropper shows what it hits.** A small label follows the eyedropper over the picture
  with a swatch and the value of the color under its tip.

- **File name placeholders with the time.** Batch rename has two more placeholders to click
  that insert the file date and the date taken together with the time of day. The file name
  field of the other batch dialogs now offers the same placeholders to click.

- **Batch rename resolves name conflicts.** If two pictures would get the same name, or the
  name is already taken in the folder, the next one gets _1, _2 and so on instead of stopping
  the rename.

- **File extensions in lower case.** Saving under a new name, exporting, batch converting,
  renaming and batch renaming now write the file extension in lower case, so IMG_0001.JPG
  becomes IMG_0001.jpg. Files saved in place keep their name.

### Fixes

- Context menus and footer menus always open at the size of their content and from the top:
  no more changing sizes, missing rows or scroll arrows when the menu fits on the screen.
  The folder tree menu no longer shows doubled or trailing separator lines, and the dialogs for
  setting the capture date and the place are wide enough for their options.

- Picking exactly black, white, red or another standard color in a color picker no longer
  falls back to a different color. This affected the background of a new document, of a
  collage and behind transparent areas, the replace color brush and the gradient map.

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

- **Missing models are offered where they are needed.** A function that needs a model which is
  not downloaded yet now asks whether to download it when you try to use it, instead of being
  greyed out. Only the file that function needs is fetched, and the same window shows the progress.
  Where the model can use the graphics card and acceleration is off, it offers to switch it on,
  checks the card and shows the result.

- **The graphics card check is remembered.** Settings no longer show a card as unchecked after
  every restart; each new check replaces the result.

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
