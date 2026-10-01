## Unreleased

### What's new

- The healing brush now looks at the spot before repairing it. On an even surface it only fills
  from even areas, so removing one line of text no longer drags in pieces of the lines above.

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

