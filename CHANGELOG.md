## Unreleased

### Changes

- Extensions are now downloaded in the settings, section Extensions, with Download extensions.
  The list shows every extension in the FerrumPix directory with a short description, a sample
  picture and a link to the pull request it came with. Extensions you have are marked as
  installed, and a newer version is offered as an update. FerrumPix no longer comes with
  extensions; whoever showed one of the four from 0.9.87 finds it ticked in the list.

- Two new extensions by Spoilerhead can be downloaded: a vignette and a graduated filter, both
  of which keep bright highlights.

- Pasting adjustments onto JPG, PNG or WebP pictures can now save a copy next to the original
  instead of overwriting it. The copy is named by the pattern of Save as, for example
  picture_fx.jpg. Overwriting is still offered as the second choice.

### Fixes

- The quick preview with SPACE showed the default version of a RAW after a moment instead of the
  variant whose tile was selected, for example the colour picture instead of the black and white
  variant.

## 0.9.87

### Changes

- Sharpening has a third method, High Pass. It works like a high pass layer blended in linear
  light, done in one step, and brings out the finest detail. Strength 100 equals that blend at
  full opacity, and Radius reaches down to very small values.

- FerrumPix can now load extensions: small files that add a tool with its own sliders, for
  example a black and white conversion or a toning. They are loaded and removed in the settings,
  section Extensions. As soon as one is loaded, the editor shows the Extensions tool in the
  Adjustments group, with one group of sliders per extension. Pictures keep the settings of an
  extension even where it is not installed. How to write one is described in
  docs/EXTENSIONS.md, together with four examples.
  Many thanks to Spoilerhead, whose black and white conversion Silicon Bonk and split toning
  Fat Toni gave the reason for extensions and are among the examples.

- FerrumPix comes with four extensions: the black and white conversion Silicon Bonk, the split
  toning Fat Toni, a toning with a curve and a light falloff. They are hidden at first; the eye
  in the settings, section Extensions, brings them into the editor.

- Extensions run in a fixed order that the extension files set, no longer in the order they were
  switched on, so two pictures with the same settings always look the same. An extension can
  link to its description, which the settings show as a button.

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

- Highlights and Shadows set in the same direction, both up or both down, now strengthen each
  other instead of holding each other back.
  
- When another program changes the XMP sidecar of a picture at the very moment FerrumPix is
  about to save a rating, keywords or other details into it, FerrumPix now leaves the file as
  the other program wrote it instead of replacing it with its own version.

### Fixes  

- On some Windows computers, Denoise with model and the check of the graphics card in the
  performance settings hung without end since 0.9.86-3. The models run on the graphics card as
  in 0.9.86-2 again. If a graphics card ever stops answering, FerrumPix now gives up after a
  minute, remembers it and lets the models run on the processor instead.

- With the Move button on the editor stage, or while holding SPACE, dragging on a selected object now
  moves the view as everywhere else, instead of moving or selecting the object. Zoomed in, where a
  selected object often fills the view, the button seemed to do nothing.

- A sidecar file with picture variants that contains something this version does not know,
  for example from a newer FerrumPix, is no longer rewritten without it. It is left as it is.
