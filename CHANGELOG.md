## Unreleased

### Changes

- Adding haze in the editor now tints the haze with the colour of the light in the picture, so a
  sunset gets a warm haze instead of always the same cool grey. This applies to new edits; pictures
  you edited before look exactly as they did.

- The editor's tool bar can now stand on the left, on the right, or on the side of the adjustments
  panel (Settings, Editor). A new automatic mode keeps it narrow and opens it over the picture when
  the pointer rests on it, without taking room from the picture. The collapse button stays where it
  was and pins the bar as before; out of the box the bar is on the left with icons and text, as it
  always was.

### Fixes

- EXR files are read with an updated library that closes a security issue with damaged files.

- Videos that start with a black frame get a real picture as their tile again, and the viewer
  shows that picture until you press play instead of a black area. If the picture of a video does
  not come up when it is opened, the viewer now tries once more by itself.

