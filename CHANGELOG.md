## Unreleased

### Changes

- Filmstrip: two new settings, both off by default. One shows the edited mark of the gallery
  tiles on the filmstrip too, the other keeps the selected picture in the middle of the
  filmstrip, so the strip moves instead of the selection.

- Windows on ARM: the portable package now plays videos and opens HEIC and JPEG XL files, like
  the x64 package.

- Lens correction knows two more Nikon lenses, among them the Nikkor Z 24-70mm f/2.8 S II.

- Text: a new All caps button next to bold and italic shows the text in capitals while keeping
  what you typed. It comes back from PSD files and goes out to them.

### Fixes

- Film negatives: a converted negative no longer turns pale on its own a moment after the
  conversion is switched on, for color and black and white film alike.

- RAW+JPEG pairs: marking a picture as kept or rejected in the viewer now marks both files, like
  in the gallery. The "Kept only" filter shows the whole pair even if only one file was marked
  before, so copying the kept pictures brings the JPEGs along again. Stars, hearts and color
  labels set in the viewer also apply to both files.

- PSD: text set in an OpenType font with CFF outlines keeps its font when the exported file is
  opened in Photoshop. A flat PSD from Photoshop with a saved selection no longer opens with
  holes in it.

- PSD import: layer masks from some Photoshop files no longer slide off their layer, so those
  layers no longer go missing. An image layer with a vector mask keeps its mask. Text with mixed formatting keeps
  its font, size and position, Times New Roman is found again, and files made with some design
  templates now ask again whether text should stay editable.

