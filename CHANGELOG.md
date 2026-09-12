## Unreleased

### What's new

- A new application icon, used everywhere the program shows up: window and task bar, the
  start menu, the dock and the installers.

- FerrumPix is in the Microsoft Store. Windows installs it and keeps it up to date for you.

- Picking a layer no longer changes your tool. Retouching, drawing, selecting, masking and the
  sliders all stay where they are, and the layer you clicked simply becomes the new target. Only
  inserted objects still bring up their own settings, because that is where you edit them.

- Pictures are inserted at their original size. They get smaller or larger when you say so, over
  the handles or the size fields, and not before.

- Hold CTRL while dragging an object to move it freely, without the guides pulling it into place.
  ALT does the same as before.

### Fixes

- People found on pictures from an Immich server now show their faces. The tiles in the people view
  stayed empty, and a person you had named yourself turned up none of their other pictures.

- While you bend an object, its frame now shows the shape the object really takes. It used to draw
  a smoother curve than what appeared after letting go.

- Smaller things: projects saved as a project file keep a slightly larger preview, so the gallery
  and the viewer stay sharper when you zoom in, and the settings now name G'MIC among the separate
  programs FerrumPix can use.

## FerrumPix 0.9.43

### What's new

- Pictures can be saved as TIFF, in *Save as* and in every batch run that writes new files. The
  shot data and a copyright note go along.

- *Open with* hands a picture to another program. You name the programs in the settings, and they
  appear in the context menu of the gallery and the viewer. They always get the original file,
  without your edits.

- With G'MIC installed, the editor can pass the picture to it from the footer menu. Whatever you
  apply there comes back as a new layer on top of your photo.

- In the viewer, `Z` switches between 100 % and fit, and `SHIFT` with the arrow keys moves around a
  zoomed picture. Zooming with the keys or the buttons keeps the middle of the view in place, so
  100 % now shows the centre of the picture instead of its top edge.

### Fixes

- Saving in the editor shows a progress bar while the file is written, and the editor no longer
  flashes "no image" while it opens the saved file. Both were most noticeable with TIFF.

- A video from an Immich or Nextcloud server no longer takes the app down with it. The fix in 0.9.42
  did not hold: such a video is only a file once it has been downloaded, so the viewer runs through
  its picture path twice, and the second pass started it again while the first was still drawing.
  Local videos were never affected.

## FerrumPix 0.9.42

### What's new

- The Adjustments panel can be made wider. Drag its inner edge and every slider grows with it, which
  is what you want for fine work: the same mouse movement now changes the value in smaller steps.
  FerrumPix remembers the width you picked.

- The strength of the AI denoiser now starts at 70 and is remembered. It used to sit at 30 every
  time you opened FerrumPix, and it is easy to miss next to the two buttons - so a run cost minutes
  and showed less than the model can do. Whatever you set is kept for next time.

- Noise reduction keeps your detail. The luminance slider now uses a filter that smooths flat areas
  and leaves edges alone, instead of blurring everything equally: it removes more noise than before
  and a bow tie stays a bow tie. It also does something from the very first tenth of the slider,
  where the old one did nothing at all, and it takes colour noise with it as it goes. The two
  earlier methods are still there in the dropdown, and anything you edited before opens exactly as
  you left it.

- Starting FerrumPix with `--debug` now shows what it is doing. It writes to the window you started
  it from, beginning with where the log file is and ending with the code it exited with, so a start
  that fails is no longer silent.

- The target shown at the bottom left names the layer you are working on instead of what kind of
  layer it is. A selected image layer used to read the same as the whole picture.

### Fixes

- A video from an Immich or Nextcloud server plays instead of taking the app down with it. Such a
  video only becomes a file once it has been downloaded, and the viewer started it a second time on
  the way there; on macOS that ended the program on the spot. Videos on your own disk were never
  affected, and repeating a video still works.

- Denoising and removing an object now fit themselves to the graphics card. Both used to ask the
  card for more memory than a small one has, and on such a card the app did not report a problem,
  it closed. FerrumPix now asks the card how much memory it has and computes in smaller pieces when
  there is not much, which costs a little detail on those cards and nothing at all on the others.

