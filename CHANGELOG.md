## Unreleased

### What's new

- Four more adjustments that also work on adjustment layers. Under Color, a channel mixer
  mixes each channel anew from red, green and blue, for swapped colors, a cast that one channel is
  missing, or black and white from your own channel mix. A gradient map turns the brightness
  of the picture into a gradient between two colors, for duotone or false color looks. Under
  Effects, posterize reduces each channel to a few flat steps, and threshold turns the
  picture into pure black and white.

- **Stroke a selection.** The selection tool draws a line along the edge of the selection, inside,
  centered or outside, in a color and width of your choice. It lands on its own picture layer, so it
  can be moved, faded or deleted like any other.

### Fixes

- Shrinking a picture a lot, such as a full film scan down to a size for the web, no longer turns
  film grain and fine patterns into coarse stripes. This applies to resizing, exporting with a size,
  printing and collages.

- Exported PSD files carry an sRGB color profile. Before, programs that assume their own working
  space without one could show them too saturated. They also keep the resolution of the original
  and, when metadata is kept, its XMP data such as rating and keywords.

- PSD files in indexed color open, transparent color included, files with more than 300 layers
  keep their layers, and nested groups that some versions write differently no longer fall apart
  into loose layers.

- Lens correction no longer leaves colored streaks in the corners. Where the correction would
  reach past the edge of the picture, it now enlarges the picture just enough to fill the frame,
  in the editor and in the viewer.

- Every step in the history carries its own name. Rotating and flipping the whole picture, the
  perspective sliders, mask density, the feather of a selection, the opacity and visibility of
  adjustment layers and groups, nudging a layer and resetting the lens correction showed up as
  just "Adjustment" before.

- Double-clicking a channel mixer slider returns it to its starting value, 100 on its own channel.

- Shadow and glow no longer show up in every tool after opening a picture while a new object
  was still armed for placing.

- Color buttons in the editor panels sit at the right edge, like the number fields, so a longer
  label such as "Background color" no longer runs into them.

