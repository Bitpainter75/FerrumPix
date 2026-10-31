## Unreleased

### Changes

- **Import from phones and cameras:** phones and cameras connected over USB now show up as
  sources in the import, each storage of a phone on its own. On KDE they are read through KDE's
  own device service, elsewhere through the desktop's device mounts. Every picture is still
  checked after copying.

- **Sharper thumbnails:** a new setting, "High resolution thumbnails", stores thumbnails twice as
  wide, so the largest gallery tiles no longer look soft. It needs more disk space and memory;
  switching it off again replaces them with the smaller ones.

- **Slideshow you can pause:** SPACE or the new button pauses the slideshow without leaving it.
  In fullscreen a small control bar appears when you move the mouse and stays while paused, with
  previous, pause, next, zoom and exit. While paused you can zoom in and drag the picture around.
  Touching the top or bottom edge of the screen brings up the filmstrip to jump to any picture.

- **Keyboard shortcuts on F1:** F1 opens the list of keyboard shortcuts from anywhere, also in
  fullscreen. It is the same list as in the settings, now including the slideshow and fullscreen
  keys.

- **Filmstrip on any side:** in the settings the filmstrip can sit below, above, left or right of
  the picture, separately for the viewer and the editor.

### Fixes

- **Slideshow timing after skipping by hand:** moving to another picture yourself now starts the
  full slideshow interval again, instead of showing the next picture after whatever was left of
  the old one.

- **Show the mask only stays on while painting:** brushing on a mask no longer switches back to
  the red overlay over the photo, so you can see exactly what is left to remove. Parts already
  brushed away no longer turn red again on the next stroke.

- **Large saved searches stay responsive:** while a saved search with many results fills the
  gallery, it no longer re-sorts everything after each small batch, so the window no longer
  stalls on very large collections.

