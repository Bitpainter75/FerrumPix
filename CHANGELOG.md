## Unreleased

### Changes

- In the automatic object selection, a click inside an existing selection now selects the
  object you clicked instead of starting to move the selection.

- A sidecar file with picture variants that contains something this version does not know,
  for example from a newer FerrumPix, is no longer rewritten without it. It is left as it is.

- FerrumPix can now load extensions: small files that add a tool with its own sliders, for
  example a black and white conversion or a toning. They are loaded and removed in the settings,
  section Extensions. As soon as one is loaded, the editor shows the Extensions tool in the
  Adjustments group, with one group of sliders per extension. Pictures keep the settings of an
  extension even where it is not installed. How to write one is described in
  docs/EXTENSIONS.md, together with four examples.
  Many thanks to Spoilerhead, whose black and white conversion Silicon Bonk and split toning
  Fat Toni gave the reason for extensions and are among the examples.

- Extensions can offer a choice from a list and a help text for each control, and can work with
  the position in the picture, for vignettes and gradients of their own. A fourth example shows
  this: a light falloff, round or from one side.

- Extensions can be hidden in the editor with the eye in the settings, and in the editor each
  extension has the eye to compare and the reset button in its header like the other groups. A
  slider goes back to its default with a double click, color fields and curves have their own
  reset button.

- Extensions can offer color fields, curves and presets, and they work on mask and selection
  layers and on objects like the other adjustments. A third example shows a brightness curve
  with toning in two colors and the presets Sepia, Selenium and Cold.

- Sharpening has a third method, High Pass. It works like a high pass layer blended in linear
  light, done in one step, and brings out the finest detail. Strength 100 equals that blend at
  full opacity, and Radius reaches down to very small values.

- When another program changes the XMP sidecar of a picture at the very moment FerrumPix is
  about to save a rating, keywords or other details into it, FerrumPix now leaves the file as
  the other program wrote it instead of replacing it with its own version.


