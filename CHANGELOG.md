## FerrumPix 0.9.51

### What's new

- **One channel at a time in the histogram.** Below the histogram you can pick RGB, red, green,
  blue or luminance, in the info panel as well as in the editor. Each adjustment tool remembers
  its choice.

- **Clipping warning from anywhere with sliders.** Besides its checkbox under Light, the clipping
  warning now has a small button in the analysis panel. With that panel shown, it is at hand in
  all adjustment tools, from light to the tone curve, and stays on while you move between them.
  J switches it on and off.

- **Pen pressure and smoothing.** With a pen, light pressure paints thinner; this works for the
  brush, the eraser and the mask brush and can be switched off in the settings. A new Smoothing
  slider steadies strokes and takes out small wobbles, for the pen and the mouse alike. It starts
  at full strength and can be turned down.

- **Change a selection.** Expand, contract, smooth or turn a selection into a border by a set
  number of pixels. Corners stay round when a selection grows.

- **Remove red eyes.** In the retouch tool, switch it on and click each eye with the brush circle.

- **Insert objects without clicking into the picture.** Text, QR codes, shapes and symbols have an
  insert button that places them in the middle of the picture, just like pictures already had.
  Handy when a layer covering the whole picture would otherwise catch the click.

### Fixes

- The brush no longer takes on the outline colour of a selected layer. Selecting a merged layer
  could make it paint invisibly, and a colour picked for the brush no longer gives the layer an
  outline.

- Text watermarks and other text that reaches past its frame now show in the editor where they
  end up in the saved picture, and merging layers no longer cuts off the part that sticks out.

- With a layer selected, the draw tool shows only the brush settings instead of listing the brush,
  shadow and glow twice.

- A colour picked with the eyedropper now paints onto the photo exactly as it looked, even with
  exposure or other sliders changed.

- Painting on a layer is much quicker on large pictures.

- The tone curve starts every picture in the RGB channel instead of the channel you last used on
  another picture.

- Keyboard shortcuts no longer stop working after a button, panel or menu you just used goes
  away or is briefly disabled. This affected the gallery, the viewer and the editor alike.

- After typing into a number field in the editor, ESC and ENTER leave the field, so the editor's
  keys work again right away. The same happens when you touch a slider with a pen.

- The places and people filters appear as soon as you turn them on in the settings, without
  restarting FerrumPix, and place names for older pictures are filled in right away.

- While FerrumPix still reads where the pictures of a folder were taken, the map says so and shows
  them as they come in, instead of an empty world map. Pictures that arrive later no longer move
  the map away from where you are looking.

- After switching folders, the strip below the map no longer shows empty tiles from the previous
  folder.

