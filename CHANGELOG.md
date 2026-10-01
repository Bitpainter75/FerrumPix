## 0.9.58

### What's new

- The healing brush now looks at the spot before repairing it. On an even surface it only fills
  from even areas, so removing one line of text no longer drags in pieces of the lines above.

- **Calmer photo wall.** Stars, heart and the other icons on a photo wall tile now only show
  while the mouse is over it. A new setting turns this off again.

- **Filmstrip in the photo's shape.** A new setting lets the filmstrip tiles follow each photo's
  aspect ratio, so portrait photos stand narrow and landscape photos wide.

- **Adjustable filmstrip spacing.** The space between filmstrip tiles can now be set in the
  settings, just like in the gallery. Both now start a little tighter.

- **Settings from the gallery toolbar.** The gallery now has the settings button in its toolbar,
  in the same place as in the viewer.

### Fixes

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

