## Unreleased

### What's new

- **Hardness for the clone stamp, healing brush and smudge.** A new hardness slider sets how soft
  or hard the edge of these tools is, just like for the paint brush. It starts where the edge has
  always been, so nothing changes until you move it.

- **Brush sizes are remembered.** The brush, eraser, smudge, healing brush and clone stamp each
  keep their size and hardness when you switch tools, open another picture or restart the app.

## 0.9.58

### What's new

- The healing brush now looks at the spot before repairing it. On an even surface it only fills
  from even areas, so removing one line of text no longer drags in pieces of the lines above.

- **Calmer photo wall.** Stars, heart and the other icons on a photo wall tile now only show
  while the mouse is over it. A new setting turns this off again.

- **Filmstrip in the photo's shape.** A new setting lets the filmstrip tiles follow each photo's
  aspect ratio, so portrait photos stand narrow and landscape photos wide.

- **Adjustable filmstrip spacing.** The space between filmstrip tiles can now be set in the
  settings, just like in the gallery, and starts a little tighter than before.

- **Align and distribute layers.** In the editor, SHIFT+click adds more layers to the selection
  right on the photo. Buttons in the top bar then line them up left, right, top, bottom or
  centred, against each other or against the picture, and spread three or more out evenly.

- **Settings from the gallery toolbar.** The gallery now has the settings button in its toolbar,
  in the same place as in the viewer.

### Fixes

- Undo and redo in the editor now keep the selected layers selected, also when several were
  selected together. Before, nothing was selected afterwards.

- Resizing several selected layers at once now keeps the frame around them in shape. Before, the
  frame could jump to a far too tall or wide size as soon as you pulled a handle.

- With a layer selected, color range, luminance range, depth and subject selection now stay
  within that layer instead of picking matching areas across the whole photo. Filling such a
  selection, or turning it into a mask or selection layer, now lands right above that layer and
  only affects it, instead of ending up hidden in the photo underneath.

- A layer limited to the layer below now also respects that layer's mask: where the mask hides
  the layer, nothing shows through or gets changed. Selections made on a layer with a mask stay
  within its visible part as well.

- An adjustment limited to a layer that is itself limited to the layer below now only affects
  what is actually visible of it.

- Shutter speeds like 1/20, 1/50 and 1/100 now show as fractions in the gallery and the info
  panel, instead of as decimals like 0,05.

- The info panel now always shows the real dimensions of a RAW photo, also when quickly
  switching between photos. Before, it sometimes showed the smaller size of the preview embedded
  in the file, and that could end up in the catalog as well. Portrait photos are now also stored
  upright in the catalog, so sorting and filtering by width work as you see the photos.

