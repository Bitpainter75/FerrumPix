## Unreleased

### Changes

- **Import from phones and cameras:** phones and cameras connected over USB now show up as
  sources in the import, each storage of a phone on its own. On KDE they are read through KDE's
  own device service, elsewhere through the desktop's device mounts. Every picture is still
  checked after copying.

- **Sharper thumbnails:** a new setting, "High resolution thumbnails", stores thumbnails twice as
  wide, so the largest gallery tiles no longer look soft. It needs more disk space and memory;
  switching it off again replaces them with the smaller ones.

### Fixes

- **Show the mask only stays on while painting:** brushing on a mask no longer switches back to
  the red overlay over the photo, so you can see exactly what is left to remove. Parts already
  brushed away no longer turn red again on the next stroke.

- **Large saved searches stay responsive:** while a saved search with many results fills the
  gallery, it no longer re-sorts everything after each small batch, so the window no longer
  stalls on very large collections.

