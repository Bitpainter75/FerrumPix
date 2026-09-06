## FerrumPix 0.9.39

### What's new

- **RAW files get a real temperature slider.** For a RAW photo the white balance now reads in
  Kelvin, starting at the value your camera recorded, the way a photographer thinks about light.
  Behind it the white balance is no longer a nudge to red and blue but a proper conversion between
  two lights, computed in linear light: grey stays grey, green takes part, and the brightness of
  the picture no longer creeps along when you move the slider. The presets in the list are real
  illuminants now, so Daylight is daylight rather than "a little warmer". Other file types keep
  their familiar slider, because a photo without camera data has no shot temperature a Kelvin
  number could refer to. Photos you already edited keep the look you saved.

- **A Lightroom preset arrives in the light you shot in.** Presets for RAW files state the colour
  temperature as a fixed number, and that number only means something next to the light the photo
  was taken in. Where a preset did not bring that reference along, FerrumPix had to assume daylight,
  which went wrong for pictures taken under lamps or at night. It now reads the white balance of the
  shot from the RAW file itself, so such a preset lands where it was meant to. This works when you
  apply a preset in the editor and when FerrumPix picks up develop settings from a sidecar; over a
  whole selection at once it still uses the old estimate.

- **A picture from the filmstrip becomes a layer.** Drag a thumbnail from the strip at the bottom of
  the editor onto your photo and it is placed there as a picture layer, at the spot where you drop
  it. The same as dropping a file from the file manager, only without leaving the application.

- **On macOS the colours now match your screen.** Most Mac screens can show more colours than a
  photo contains, and until now photos and the interface looked too strong on them. FerrumPix now
  tells the system what it draws, so macOS converts the colours for the screen you are using. On an
  ordinary screen nothing changes. The switch for it is in the settings and it is on.

- **More of the local models can use your graphics card now.** Removing an object, the two quick
  upscalers and face comparison were held back on the processor because they were slower on the
  card. With the updated model runtime they are not, so they now run there as well. Nothing changes
  if you have no card, or leave the setting off.

- **The model runtime does not phone home.** Its newest version brings its own reporting along on
  every system, not just Windows. FerrumPix switches it off before the runtime starts, so nothing
  about your photos or your machine is collected or sent.

- **The folder tree starts where your photos are.** The folders you have added for the catalog now
  sit at the top of the tree as their own starting points, with their subfolders. The tree no longer
  begins at your home folder and the root of the drive alone.

- **On macOS the application is called FerrumPix again.** The menu bar named it after the toolkit it
  is built with, and its About entry opened that toolkit's window. It now carries its own name and
  its own menu.

- **Edge and extent of a clicked selection are set in pixels now.** Both were percentages of
  something you could not see: the edge did nothing visible over nine tenths of its travel, and the
  extent went from nothing to everything within a third of it. The number you set is now the number
  of pixels you get, the soft edge only runs outwards so the selection no longer loses ground on the
  inside, and a double click puts either slider back on its default.

### Fixes

- **A saved photo no longer carries the old preview picture inside it.** Cameras and phones put a
  small copy of the picture into the file, and it travelled along unchanged when you saved: a file
  manager showing that copy showed your photo without the edits, in the old size. The same went for
  the width and height noted in the shot data, which still described the original after a crop or a
  resize. Both now match the file you actually saved.

- **The grouped gallery view scrolls as smoothly as the grid.** It now builds only the tiles that
  are actually on screen and hands them on as you scroll, instead of rebuilding them on every
  movement. Large folders and long date ranges stay responsive, and the scrollbar no longer shifts
  under your hand.
