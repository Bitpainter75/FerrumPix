## FerrumPix 0.9.31

### What's new

- **Photoshop files in CMYK open with their layers.** Until now they came in flat, as one finished picture, and only RGB and greyscale files kept their layers.

### Fixes

- **Long retouch strokes stay responsive.** Smudging, healing and cloning got slower the longer you drew. A stroke is now as quick at the end as at the start.

- **Pictures with local corrections redraw much faster.** A correction layer used to rebuild its mask and run over the whole picture on every redraw, however small the brushed or graduated area was. The result looks exactly as it did before.

- **Thumbnails on a spinning hard disk no longer fight each other.** Too many pictures were read at once, and on a drive with a moving head that makes it slower rather than faster. FerrumPix now asks what kind of drive your pictures live on. Pictures on a server are unaffected.

- **Saving a large JPEG is quicker.** Getting at the camera data, the keywords and the copyright notice no longer means reading the whole file first.

- **A Photoshop file you have already edited opens with its layers again.** The sidecar file next to it used to push the layers aside, and a rating alone was enough to create one. The layers now come from the file and your edits lie on top. Browsing to such a file shows the same document as opening it does.

- **Objects can be made much smaller.** Shapes, images and arrows were held to a size that made a small mark or a logo impossible on a large picture. The outline can now also be drawn twice as thick as before.

- **Smaller things.** The slideshow and adjust buttons in the viewer have a frame, so it is clear where they can be clicked, and browsing a folder whose pictures carry a copyright notice is quicker.
