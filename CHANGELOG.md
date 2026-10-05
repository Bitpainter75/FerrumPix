## 0.9.61

### What's new

- Four more adjustments that also work on adjustment layers. Under Color, a channel mixer
  mixes each channel anew from red, green and blue, for swapped colors, a cast that one channel is
  missing, or black and white from your own channel mix. A gradient map turns the brightness
  of the picture into a gradient between two colors, for duotone or false color looks. Under
  Effects, posterize reduces each channel to a few flat steps, and threshold turns the
  picture into pure black and white.

- **Stroke a selection.** The selection tool draws a line along the edge of the selection, inside,
  centered or outside, with its own color, width, hardness and round or sharp corners. A selection
  can also cast a shadow and glow, like an object, around the outside while the selected area
  itself stays as it is. Fill, stroke, shadow and glow of a selection are live: tick them on,
  every change shows at once, they come back when the selection layer is picked again, and they can
  be switched off. While an object is selected, its own groups show instead.

- **Fill and stroke work the same everywhere.** Text, watermarks, shapes, symbols and the selection
  share the same fill and stroke groups. A stroke on any object can now sit inside, centered or
  outside, fade out softly and have round or sharp corners. Fill, stroke, shadow and glow switch on
  with a checkbox in their header and only open when they are in use.

- **Gradients with any number of colors.** A fill can be linear, radial, angle, reflected or
  diamond shaped. Click the gradient bar to add a color, drag to move it, drag it off to remove it;
  each color can be partly transparent. Size, center and repeating can be set, and a list of
  ready-made gradients sits next to the bar, where your own can be saved too.

- **Inner shadow and inner glow.** Shadow and glow can lie outside, inside or both. Inside, the
  shadow falls along the inner edge that faces the light and the glow shines from the edge into
  the shape, on objects, text, brush strokes and selections alike.

### Fixes

- Linear and radial fills now show exactly the colors you set: a radial fill reaches its outer
  color at the edge of a circle or text instead of stopping short of it.

- Text and symbols keep the same stroke width whether "Blend the outline too" is on or off.

- Shrinking a picture a lot, such as a full film scan down to a size for the web, no longer turns
  film grain and fine patterns into coarse stripes. This applies to resizing, exporting with a size,
  printing and collages.

- Raw files from more camera models start out closer in brightness and color to what the camera
  intended.

- The status in the editor footer uses the free space up to the zoom buttons instead of cutting
  messages short.

- A text layer selected in another tool stays selected when you switch to the text tool to change
  it, the same for watermarks, pictures and QR codes. "Blend the outline too" can be switched at
  any time and now works for image layers, copied selections and image watermarks as well.

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

- A group with its checkbox ticked, such as fill, stroke, shadow or glow, stays open, and the
  title in its header lines up with the checkbox and the icon.
