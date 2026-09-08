## FerrumPix 0.9.41

### What's new

- **RAW to DNG.** Converting a selection now offers DNG as a target, so RAW files from the camera
  can be kept as raw files in a format every program reads. This one is passed to dnglab, which
  has to be installed; if it is not there, or the selection holds nothing that could become a DNG,
  the app says so instead of quietly doing nothing.


### Fixes

- Typing a width or a height is no longer interrupted. With Keep aspect ratio on, the other field
  used to be recalculated after every single keystroke, so the number you were about to type kept
  moving under your hands and it looked as if only one of the two could be set at all. The second
  field now follows once you leave the first one. This covers image size and canvas size in the
  editor as well as the width and height of the resize and export dialogs.

- Keep aspect ratio keeps working after you switch it off and on again. Once both width and height
  had been set by hand, the second field stopped following the first for good, and the two could
  only be changed one by one.

- The size presets in the editor do what they say, and the tick decides how. With Keep aspect ratio
  on, a plain number is the longer edge and UHD, Full-HD and SD fit the photo inside that frame, so
  it keeps its shape. With the tick off you get exactly those numbers, distortion included, which is
  the point of switching it off. Before, a number went into both fields and squashed the photo into
  a square, and Full-HD only set the longer edge, so the result was taller than Full-HD. SD in the
  editor now means the same 1280 by 720 as it does in the batch dialog.

- Upscaling with a model after enlarging the canvas works. The canvas kept its old size in pixels
  while the picture grew, so the result was a cutout of the enlarged photo in the old frame, and
  the dimensions in the info sidebar never changed. Everything now grows by the same factor, and
  the line that announces the new size counts the picture you see rather than the one on disk. The
  same holds for a saved edit that is applied later: reopening a raw file now shows its canvas at
  the size that fits the undeveloped photo, and applying the stored upscale puts it back up.

- Double clicking a slider lands where the slider started. Two of them went somewhere else: the
  text size dropped to the smallest size there is, and the denoise strength jumped up instead of
  going back.

- The app no longer freezes while a video is playing. Clicking a button, stepping on to the next
  item or leaving the viewer before the video had ended could lock everything up until the app was
  killed, and stepping on to a photo did it every time. This was worst on macOS, and there it is
  gone.

- Clearing up the catalogue shows what it is doing. Both ways did their work with the app standing
  still and nothing to see: cleaning the database checks every catalogue entry against the disk,
  and clearing up folders walks through every one of them, which with a few thousand folders on a
  network drive takes minutes. Both run in the background now, count their way through and can be
  stopped. Stopping the database clean removes nothing at all; stopping the folder clean keeps what
  it managed and says so.

- Progress bars actually appear. The bar for the catalogue index, the face scan and the clean-up
  was never drawn, so a long run showed a line of text and nothing else.

- A video no longer sometimes stays black. Two things could leave you with sound and no picture: if
  the picture area was ready a moment later than the player expected, the file was never handed
  over at all; and coming back to a video you had just left showed nothing, because the picture was
  still going to the area from before. Both are gone.

- Playing video no longer piles up memory. Every resize and every switch to full screen left the
  previous frame buffer behind.

- The Catalog section in the settings is no longer German. The folder list, the two action menus
  and the search status line stayed in the source language whatever you had picked; they are
  translated now, and so are the two-line tooltips (the colour ranges in the colour mixer, for one),
  the keyword chips, the layer rows, the people tiles and the preset lists, which had the same
  problem.

- On macOS a video no longer opens in a window of its own. It plays inside the picture area, the
  way it does on Windows and Linux, so it cannot slip behind the main window when you pick the next
  one, there is no close button on it that takes the app down with it, and the playback bar sits on
  top of the picture where you can see it.

