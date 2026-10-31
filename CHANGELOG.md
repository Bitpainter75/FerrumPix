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
  Touching the edge of the screen where the filmstrip is set to appear brings it up to jump to any
  picture.
  The bar also switches random order and the filmstrip on and off and sets how long each picture
  stays, and a bar at the top rates, marks as favourite, colour labels and keeps or rejects the
  picture on screen.
  While paused the mouse wheel zooms. RAW files no longer leave a black screen between pictures: the next one is
  developed while the current one is on screen.

- **Keyboard shortcuts on F1:** F1 opens the list of keyboard shortcuts from anywhere, also in
  fullscreen. It is the same list as in the settings, now including the slideshow and fullscreen
  keys.

- **Keep and reject next to the heart:** in the info panel, in every context and footer menu and
  on both sides of the viewer's comparison, two buttons next to the stars and the heart mark the
  pictures as kept (green) or rejected (red); pressing the same one again takes the mark back. The separate menu entry for rejecting is gone.
  The small cross for clearing stars or colour labels is gone too, since clicking the same star
  or colour again already clears it.

- **Filmstrip on any side:** in the settings the filmstrip can sit below, above, left or right of
  the picture, separately for the viewer, the editor and fullscreen. All filmstrip settings now
  have a section of their own.

### Fixes

- **Clicks no longer get lost under a tooltip:** buttons in the footers and the slideshow bar, and
  tiles when selecting in the gallery, sometimes ignored a click while their tooltip was showing.
  Tooltips now let clicks through.

- **Ratings show up everywhere at once:** stars, favourites and colour labels given in the viewer,
  the slideshow or the editor now appear right away on the gallery tiles, in the filmstrips and in
  the info panels.

- **Month names in your language:** folder and file name patterns with dates, in the import
  targets, batch rename and the editor's name suggestion, now write month and day names in the
  language of your region, for example "10 ottobre" instead of "10 October".

- **Slideshow timing after skipping by hand:** moving to another picture yourself now starts the
  full slideshow interval again, instead of showing the next picture after whatever was left of
  the old one.

- **Show the mask only stays on while painting:** brushing on a mask no longer switches back to
  the red overlay over the photo, so you can see exactly what is left to remove. Parts already
  brushed away no longer turn red again on the next stroke.

- **Large saved searches stay responsive:** while a saved search with many results fills the
  gallery, it no longer re-sorts everything after each small batch, so the window no longer
  stalls on very large collections.

