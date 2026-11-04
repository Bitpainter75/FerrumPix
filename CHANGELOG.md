## Unreleased

### Changes

- **Similar pictures group more strictly:** every picture in a group now looks like all the
  others in it. Two different pictures no longer end up together only because a third one
  resembles both.

- **Intersect on gradient masks:** the brush in intersect mode now works on linear and radial
  gradient masks and keeps only the part of the gradient you paint over. Afterwards you can paint
  more of the gradient back in or take more away.

### Fixes

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

