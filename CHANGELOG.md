## FerrumPix 0.9.35

### What's new

- **A trackpad mode, without changing the mouse controls.** Switch it on below the drawing tablet
  setting and two fingers zoom in Viewer and Editor, a clear horizontal swipe moves through the
  filmstrip, and dragging a zoomed picture pans it. In the Viewer, hold SHIFT while dragging to
  draw a crop instead.

- **Ratings, favourites and colour labels can appear on filmstrip pictures.** They are off by
  default and can be enabled in Settings. Their badges update straight away, whether you set them
  in the info sidebar, with a shortcut or from a menu.

- **Saved LUTs and XMP presets ask before they are removed.** The question names the preset and
  makes clear that only the saved entry is removed; the file itself stays where it is.

- **The history of a picture, in the layers panel.** A second tab lists every step you have taken,
  from the original onwards, each in a row of its own with the icon of the tool it came from and its
  number: Exposure, Temperature, Crop, Brush, Mask. Every slider names itself, down to Colour
  mixer: Aqua saturation and Calibration: Red hue, so you can see which one you moved. Objects say what
  you changed on which of them, Text: Shadow strength or Image: Opacity, and so do the actions
  behind them: Grouped, Layer moved, Path point added, Added: Text. Click a step and
  the picture goes back to it, as far back as the original and forward again. Steps you have moved
  back past stay in the list, greyed out, until you change something - then the way forward from
  there is gone, as you would expect.

- **One drag on a slider is one step.** A long drag used to leave a pile of identical rows behind,
  and CTRL+Z then took the move back in pieces. The step is now written when you let go of the
  slider, and taking it back returns to where the slider stood before you touched it. A click that
  changes nothing leaves nothing behind.

- **Icons on the buttons of the editor sidebar.** Fill type under Object, Selection and Frame, the
  path a text runs along, the watermark templates, the saved adjustments, the background of the
  picture, and inverting, discarding, copying and pasting a mask or a selection: every one of those
  buttons now carries a small picture beside its word, the way the other panels already did.

  
### Fixes

- **Fullscreen is easier to leave and safer while editing.** SPACE now returns from fullscreen as
  well as ESC. You can enter fullscreen with unsaved edits; while it is open the picture cannot be
  changed, so those edits cannot accidentally be left behind.

- **Touching a path point is not a change.** Clicking a point or a handle in the path tool, without
  moving it, marked the picture as edited, and closing then asked whether to save something that was
  never altered. Only an actual drag counts now.

- **A path drag ends when the pointer does.** If the pointer was taken away mid-drag, by a window
  change, a popup or a tablet that stopped reporting, the drag stayed on inside and only ended at
  some later movement.

- **The history stays open while you work.** Picking another tool used to close the history tab and
  put the layer stack back, so you had to open it again after every tool.

- **The tone curve now sits on the picture you are working on.** The histogram behind the curve
  stayed at the state of the file, so after the first slider it no longer answered the question you
  were asking it.

- **The copyright field is back in the batch dialogs that overwrite.** Resize, Apply filter and
  Watermark hid it together with the target settings when you kept the originals - the very runs
  where you are most likely to set it.

- **The Save button only lights up when there is something to save.** It carried the accent colour
  at all times, and a button that is always bright says nothing.

- **Apply looks the same everywhere.** In the three warp tools - grid, envelope and lines - the
  button that makes the work stick was a plain one and went unnoticed among the sliders. It now
  carries the accent colour, as it does under Crop and Image size.

- **The clipping warning ends with the tool.** It used to stay on after you switched away from the
  adjustment sliders, marking a picture you were working on in a different way, with its tick box
  out of sight.

- **The calibration sliders can be undone.** The seven sliders under Calibration wrote their value
  and nothing else: CTRL+Z went straight past them and took back the step before instead. They now
  go the same way as every other slider.

- **Cleaning the database now also clears faces.** Deleting a picture left its faces behind, so a
  person kept counting photos that were no longer there, and Clean database did not help: it only
  ever cleared ratings, labels and keywords. It now clears faces and scan marks with them, including
  those of pictures that only ever went through the face search. Indexing your watched folders tells
  you how many entries point at files that are gone, so you know when it is worth running.

- **Sliders no longer stick to the pen.** Lift a pen off the tablet and the letting go often never
  reaches the application. A slider you had just touched then followed the pen around and changed
  its value as you passed over it, and only a click somewhere else set it free. Sliders, curves, the
  timeline, brush strokes and every drag on the editor stage now end as soon as the pointer moves
  with no button held. Anything you had drawn or dragged is kept.

