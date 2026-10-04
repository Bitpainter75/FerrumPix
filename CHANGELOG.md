## Unreleased

### What's new

- Four more adjustments that also work on adjustment layers. Under Color, a channel mixer
  mixes each channel anew from red, green and blue, for swapped colors, a cast that one channel is
  missing, or black and white from your own channel mix. A gradient map turns the brightness
  of the picture into a gradient between two colors, for duotone or false color looks. Under
  Effects, posterize reduces each channel to a few flat steps, and threshold turns the
  picture into pure black and white.

### Fixes

- Exported PSD files carry an sRGB color profile. Before, programs that assume their own working
  space without one could show them too saturated. They also keep the resolution of the original
  and, when metadata is kept, its XMP data such as rating and keywords.

- PSD files in indexed color open, transparent color included, files with more than 300 layers
  keep their layers, and nested groups that some versions write differently no longer fall apart
  into loose layers.

- Color buttons in the editor panels sit at the right edge, like the number fields, so a longer
  label such as "Background color" no longer runs into them.

