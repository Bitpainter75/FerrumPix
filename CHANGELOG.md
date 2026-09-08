## FerrumPix 0.9.41

### What's new


### Fixes

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

