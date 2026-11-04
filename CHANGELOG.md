## Unreleased

### Changes

- **Similar pictures group more strictly:** every picture in a group now looks like all the
  others in it. Two different pictures no longer end up together only because a third one
  resembles both.

- **Settings open right away:** the first section shows at once and the others fill in below it
  while you look, instead of the whole page being built in one go.

- **Show only kept pictures:** the gallery filter for kept and rejected pictures can now show
  only the ones you marked as kept, so the keepers from a card can be selected and copied in one
  go. With stacks turned on, the JPEG next to a raw file goes along.

- **More watermark settings when exporting:** besides size and position, the margin from the
  corner and the opacity can now be set for each export without changing the saved watermark.

- **Intersect on gradient masks:** the brush in intersect mode now works on linear and radial
  gradient masks and keeps only the part of the gradient you paint over. Afterwards you can paint
  more of the gradient back in or take more away.

### Fixes

- Export: the option to keep the watermark size now shows up whenever resizing is on, so a
  watermark no longer shrinks to a few pixels on a smaller export. The exported watermark also
  keeps its blend mode, outline and shadow from the saved watermark.

- The anchor points for watermarks, objects, canvas and collage are easier to see.

- Film negatives: turning on the negative conversion no longer keeps the camera's own exposure
  and color settings, which could wash out a scanned negative. Values you changed yourself stay.

- Gallery: after leaving the viewer, the gallery shows the picture you last looked at again
  instead of an old scroll position.

- Gallery: thumbnails that could not be read for a moment no longer stay empty, they are loaded
  again shortly after.

- Settings always open at the top instead of where you left them last time.

- Display profiles: screens that report no name, as some Windows drivers do, now show up in the
  list and can get a profile.

- Printing from the Flatpak opens the PDF viewer again instead of doing nothing.

- Raw files from the Canon EOS 350D and EOS R5 Mark II, Fujifilm X-H2, Nikon Z5 II and Sony A7 V
  open with a better matched starting brightness.

