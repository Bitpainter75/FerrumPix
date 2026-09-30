## 0.9.56

### Fixes

- Layer names that FerrumPix gives on its own, such as groups, pasted selections and mask layers,
  now follow the language of the app. A pasted selection was always called "Auswahl", and names
  kept the language they were created in.

- Copying the whole picture and pasting it back puts the copy exactly on top of the picture. It
  used to land far off to the side, named after a temporary file.

- Canon raw files taken with Highlight Tone Priority no longer open a stop too dark; the exposure
  starts where the camera meant it to be. Unedited raw files from the EOS R3, R8, M10, R5 Mark II
  and 60D also start at a better brightness.

- Image size with the aspect ratio unlocked: changing only the width or only the height now
  stretches or squeezes the picture as asked. The other side used to follow the old ratio anyway.

- EOS 1000D raw files at ISO 200 keep the detail in their brightest areas instead of cutting it
  off too early.

- Masks: the red overlay and the mask view now show the soft edge. Moving Feather on a color or
  luminance range seemed to do nothing, because both views kept showing the hard outline.

- The healing brush no longer leaves a darker or lighter patch with a visible edge on smooth
  areas such as a clear sky. The repair now takes on the brightness of its surroundings.

- Raw files from several newer cameras no longer come out with pale, washed out colors: OM System
  OM-3, Nikon Z50II, Z5II and COOLPIX P1100, Fujifilm X-E5 and GFX100RF, Panasonic DC-TZ95D, Canon
  PowerShot V1 and Samsung GX-1L. The notice about an unknown camera no longer shows for them.

- CTRL+V after copying a layer and then a selection or the whole picture pastes what was copied
  last. It used to duplicate the old layer.
