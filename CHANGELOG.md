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

- The buttons at the bottom of the adjustments panel that step to the previous and next tool now
  show the icon of that tool next to its name, the same one as in the tool bar.

- Spot removal from Lightroom now comes along when a raw picture's Lightroom settings are taken
  over, from a sidecar file or from a Lightroom catalog: clone spots keep their source, heal spots
  are filled by FerrumPix's own repair. Red eye corrections are not taken over yet. Presets never
  bring spots with them, since a dust spot belongs to one picture only.

### Fixes

- Olympus, OM System and Panasonic pictures taken with the camera's own shading compensation
  switched on no longer get their corners brightened a second time by the lens profile. Distortion
  and colour fringe correction stay as before.

- A damaged or deliberately crafted JPEG that claims to be far larger than its data can hold is no
  longer opened; before, a file of a few hundred bytes could make the app reserve gigabytes of
  memory.

- A LUT that an edit refers to is only read if it is an ordinary file of sensible size. An edit
  from somewhere else can no longer make the app read a device, hang on a special file, or contact
  a network share that is not in your own LUT list.

- An older FerrumPix, for example on a second computer or a Flathub package that is behind,
  no longer throws away what a newer one saved with an edit. The newer settings are kept when you
  rate, label or edit the picture. Older versions released before this one show such a picture
  unedited instead of overwriting it.

- On Windows, the AI model runtime no longer registers its telemetry provider with the system at
  all. Before, it was only switched off after the runtime had already started.

- Presets and sidecar files from Lightroom 3 and older, and edits imported from an old Lightroom
  catalog, now come in with the right exposure, contrast, highlights, shadows, clarity and tone
  curve. They are converted the way Lightroom itself updates them to its current process. Old
  brightness values in newer presets are no longer applied on top.

- EXR files are read with an updated library that closes a security issue with damaged files.

- Videos that start with a black frame get a real picture as their tile again, and the viewer
  shows that picture until you press play instead of a black area. If the picture of a video does
  not come up when it is opened, the viewer now tries once more by itself.

