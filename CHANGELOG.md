## FerrumPix 0.9.44

### What's new

- The editor can put a crooked photo straight by itself. *Level to the horizon*, under Transform,
  looks for the straight edges in the picture - the horizon, the edge of a house, a row of windows -
  and turns the photo until they sit level.

- Converging verticals can be pulled upright in one step. *Correct perspective automatically*, under
  Distort, finds the lines that lean together in a building shot and straightens them; the Size
  slider then covers the corners that went empty.

  Both of them write their result into the ordinary sliders, so you can nudge it afterwards like a
  value you had set yourself. And both say so when the picture already stands right, or when it has
  no straight edge to go by.

- A new application icon, used everywhere the program shows up: window and task bar, the
  start menu, the dock and the installers.

- FerrumPix is in the Microsoft Store. Windows installs it and keeps it up to date for you.

- Picking a layer no longer changes your tool. Retouching, drawing, selecting, masking and the
  sliders all stay where they are, and the layer you clicked simply becomes the new target. Only
  inserted objects still bring up their own settings, because that is where you edit them.

- Pictures are inserted at their original size. They get smaller or larger when you say so, over
  the handles or the size fields, and not before.

- Hold CTRL while dragging an object to move it freely, without the guides pulling it into place.
  ALT does the same as before.

### Fixes

- Setting the perspective with the sliders now makes Apply available. It only woke up when you
  dragged the corners in the picture, so a perspective set by the sliders could not be taken into
  the recipe at all.

- People found on pictures from an Immich server now show their faces. The tiles in the people view
  stayed empty, and a person you had named yourself turned up none of their other pictures.

- While you bend an object, its frame now shows the shape the object really takes. It used to draw
  a smoother curve than what appeared after letting go.

- Smaller things: projects saved as a project file keep a slightly larger preview, so the gallery
  and the viewer stay sharper when you zoom in, and the settings now name G'MIC among the separate
  programs FerrumPix can use.

