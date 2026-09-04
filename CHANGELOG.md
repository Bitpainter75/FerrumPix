## FerrumPix 0.9.37

### What's new

- **A crop stays adjustable in every format.** JPEG and PNG now behave like RAW, PSD and projects:
  the crop tool shows the whole picture, the frame can always be pulled open again, and the cut
  reaches the file only when you save. Until now a crop that was applied to a JPEG could not be
  widened again, although the file itself was still untouched.

- **The crop tool shows what the picture currently is.** A red dashed outline marks the confirmed
  crop while you pull the frame, so you can always see what you are changing it from. On a picture
  that was straightened after being cropped the outline is tilted, because that is how the
  confirmed area really lies.

- **Turning a selection into a mask layer shows its progress.** On a big picture this takes a
  moment, and it now runs in the background with a progress bar instead of holding up the window.

### Fixes

- **A rotation from a saved project can be corrected instead of repeated.** Opening a project and
  turning the picture again now replaces the rotation that is already there. Before, it was added
  on top, so the picture went through a second turn and its corners with it.

- **Masks open without the long wait.** Reopening a large mask on a picture whose geometry has not
  changed no longer rebuilds it point by point.

- **Painting a mask stays quick on large pictures.** Masks and selections are now kept ready to use
  while you work, instead of being packed and unpacked again for every single brush stroke.

- **A local correction costs far less than the whole picture.** A mask over a small part of a
  large photo used to take as long as one covering the whole frame, and every further mask layer
  added the same again. Now the work follows the area the mask actually reaches.

- **Painting on a mask no longer redevelops the whole photo.** Everything above the mask stays as
  it was while you paint, so each stroke shows up several times quicker on a large picture.

- **Clicking empty space in the layers panel keeps your tool.** Transform, image size and warp stay
  open; only the layer selection ends.

- **Working with masks no longer slows down with every picture.** Building the key that decides
  whether a picture has to be redrawn no longer unpacks each mask first. On a project loaded from
  file that happened over and over, including while dragging an object.

- **A crop in a project keeps showing what was cut away.** In a project whose base picture is a JPEG
  or PNG, the crop tool could fall back to showing only the remaining cut-out, so the frame could
  not be pulled open again. The picture itself was never cut.

- **What the crop frame surrounds is what you get.** On a picture that was straightened after being
  cropped, the frame and the result meant different parts of the picture. The crop now always
  applies to what you see under the frame.

- **Straightening no longer takes a part of the picture for good.** The crop tool now shows what a
  straightened picture loses at its corners, so you can pull the frame open again and get it back.
  Until now only a crop could be undone this way.

- **One Apply for the whole Transform tool.** Crop and orientation belong together — turning the
  picture moves the frame with it — so there is now a single "Apply transform" button below both
  sections instead of two that switched each other on. Enter still does the same.

- **The crop numbers belong to the picture you see.** On a picture the recipe has turned by a
  quarter, the Left slider moved the top edge and width and height were swapped against the size
  shown on the frame. Sliders, pixel fields and aspect presets now all measure along the axes of
  the picture in front of you.

- **The angle stays on the slider after applying.** You can correct it or take it back to zero,
  and it keeps replacing the same rotation instead of adding a second one. Before, the slider
  jumped to zero and the rotation you had just applied was only reachable through Undo.

- **Enlarge canvas only does what the checkbox says.** While you drag the straighten slider the
  canvas no longer grows on its own, and the crop keeps its size. What is already applied still
  shows what it cut away, so the frame can pull it back.
