## 0.9.77

### Changes

- **Welcome**: on its first start FerrumPix now greets you with a short setup: language, the look of
  the app, the models you want to download and, if you have one, the graphics card. Everything takes
  effect right away and can be changed later in the settings, where you can also run the setup again.
  Existing installations see it once after this update.

- **Title and description**: a new dialog in the Metadata menu sets title, headline,
  description, creator and credit line, for one picture or a whole selection. JPEG, PNG and WebP
  get them inside the file (JPEG also as IPTC), other formats such as RAW in an XMP sidecar. The
  fields are found by the gallery search and can be used as conditions in saved searches, the info
  sidebar can show each of them (off by default, switch them on in Settings), and an export from a
  RAW now carries the title and description from its sidecar into the new file.

- **File and catalog**: the Metadata menu can compare one picture's file and catalog side by side and
  copy or remove single values in either direction, take rating, label, pick mark and keywords from the
  files into the catalog, or write them from the catalog into the files or their sidecars.

- **Remove metadata** now asks which details to remove (everything, or single ones such as keywords,
  title and description, copyright or the place) and where: in the file, in the sidecar, in the
  catalog. Copyright, date and keywords no longer come back from the sidecar, and the info sidebar
  shows the result right away.

- **Search**: the gallery search and the text of saved searches now also find the place a picture
  was taken and its copyright notice, and saved searches have a new condition for the copyright.
  Saved searches can also look for keep and reject marks and for labels, chosen like in the info
  sidebar with heart, thumbs and colour dots, and the rating list shows stars. Every condition can
  now also ask whether a field is empty or not empty, and the search dialog is wider.

- **Import**: the list of shooting days has buttons to tick all days, none, or only the latest day,
  so a card that is rarely cleared no longer means unticking dozens of days by hand.

- Keywords in the info sidebar: pressing Enter adds the keyword, and the field is empty and ready
  for the next one.

- Gallery tiles now show a green thumbs up on kept pictures, just like the mark on rejected ones.
  A click on it removes the keep mark again. Both marks can be switched off in the gallery settings.
  Stars, heart and the EXIF, IPTC, XMP and ICC symbols on the tiles have a stronger shadow, so
  they stay readable on bright pictures.

- Info sidebar: in the editor it now sits on the far right, next to the layers panel. In fullscreen
  a new button in the control bar (or the I key) shows and hides it, and the picture makes room for
  it. Both the info sidebar and the filmstrip can be kept visible in fullscreen with a setting, and
  the buttons in the control bar remember your choice.
  The rating bar at the top of fullscreen and the slideshow can be switched off in the viewer
  settings.

- Filmstrip: two new settings, both off by default. One shows the edited mark of the gallery
  tiles on the filmstrip too, the other keeps the selected picture in the middle of the
  filmstrip, so the strip moves instead of the selection.

- Windows on ARM: the portable package now plays videos and opens HEIC and JPEG XL files, like
  the x64 package.

- Lens correction knows two more Nikon lenses, among them the Nikkor Z 24-70mm f/2.8 S II.

- Text: a new All caps button next to bold and italic shows the text in capitals while keeping
  what you typed. It comes back from PSD files and goes out to them.

### Fixes

- Questions and messages no longer show the button text or title of an earlier one, for example
  Remove where Download belongs.

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

