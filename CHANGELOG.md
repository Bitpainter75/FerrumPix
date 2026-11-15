## Unreleased

### Changes

- Pictures from raw files can now be saved with 16 bit per channel as TIFF, PNG, JPEG XL or PSD.
  Save as, export, the batch dialogs and the collage offer the bit depth when a raw file is among
  the pictures. Retouched and painted areas stay as fine as 8 bit, the rest of the picture keeps
  the full depth.

- The button that shows the pictures from all subfolders has moved to the bar at the bottom of the
  gallery, next to the button for stacks. The same switch is now also in the settings under Gallery.

- Next to it there is a new button that shows or hides folders whose drive is not connected or that
  were deleted outside the application. It is the same switch as the one in the settings under
  Gallery.

- When the bar at the bottom of the gallery gets too narrow, the slider for the size of the
  thumbnails makes room and comes back as soon as there is space again.

- Faster: adding noise or colour noise to a picture, dragging a text or shape with a blend mode such
  as soft light or linear light, sharpening, blurring and soft mask edges on large pictures,
  saving a picture with layers as a Photoshop file, and the fresh development of a raw file after
  moving exposure or highlights when the lens correction removes colour fringes. In very large
  folders, changing the sort order, a
  filter or the search text no longer makes the window stand still.

- The gallery stutters less while thumbnails arrive, after sorting or filtering, and when the
  pictures of subfolders are loaded. Its tiles are built faster.

- Thumbnails of raw files appear faster in a folder that is opened for the first time.

- The diagnostic log no longer grows without limit. At start, a log above 10 MB is shortened to
  its most recent part.

- Less memory: exporting a large picture with sharpening and noise reduction needs less of it, and
  after going through many raw files the application gives more of it back.

### Fixes

- A copyright notice with the copyright sign, or a name with an umlaut or an accent, arrived in the
  exported picture with a question mark in place of that character. The characters are now kept.

- With the pictures from subfolders shown, duplicating a picture from a subfolder put the copy into
  the folder at the top. The copy now lands next to the original.

- With the pictures from subfolders shown, the pictures of a subfolder whose drive is not
  connected, or that was deleted outside the application, were missing. They now appear from the
  catalogue like the folder itself, for viewing only.

- After many files were copied into a folder at once, the gallery could miss some of them until
  the next change. It now looks again by itself.

- When a video shows no picture or gets no thumbnail, the diagnostic log now says what the video
  player reported as the reason, such as a missing decoder.

