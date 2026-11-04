## Unreleased

### Fixes

- Film negatives: a converted negative no longer turns pale on its own a moment after the
  conversion is switched on, for color and black and white film alike.

- RAW+JPEG pairs: marking a picture as kept or rejected in the viewer now marks both files, like
  in the gallery. The "Kept only" filter shows the whole pair even if only one file was marked
  before, so copying the kept pictures brings the JPEGs along again. Stars, hearts and color
  labels set in the viewer also apply to both files.

## 0.9.76

### Changes

- **Similar pictures group more strictly:** every picture in a group now looks like all the
  others in it. Two different pictures no longer end up together only because a third one
  resembles both.

- **Focus peaking in the viewer:** sharp edges can be highlighted in red, so you see at a glance
  where a picture is in focus without zooming in. Switch it with K; it stays on for the next
  pictures.

- **More room in the gallery:** the left sidebar with sources and folder tree can be collapsed
  with the button in the footer, like the tool bar in the editor. Both gallery sidebars can be
  dragged wider or narrower and keep their width.

- **Eyedropper in the color mixer:** click a color in the picture and the mixer jumps to the band
  that moves it. It also works for black and white, where it picks by the color under the gray.

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

- Editor: a raw file opens faster. It is no longer developed a second time when its saved edit
  turns off the sensor edge crop, and the window no longer stalls while it develops.

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

