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
  docs/EXTENSIONS.md, together with two examples.

- Sharpening has a third method, High Pass. It works like a high pass layer blended in linear
  light, done in one step, and brings out the finest detail. Strength 100 equals that blend at
  full opacity, and Radius reaches down to very small values.

- When another program changes the XMP sidecar of a picture at the very moment FerrumPix is
  about to save a rating, keywords or other details into it, FerrumPix now leaves the file as
  the other program wrote it instead of replacing it with its own version.


