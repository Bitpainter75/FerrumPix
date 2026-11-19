## 0.9.88

### Changes

- New switch Remove colour fringe in Lens correction: it takes out the purple fringes that many
  lenses leave along hard, bright edges when shooting wide open. Only the colour goes, the
  brightness stays, and colourful parts of the picture away from such edges are left alone. It
  works for every file type, with or without a lens profile, and is off by default. A preset that
  removes purple fringes switches it on when lens fringe corrections are imported.

- Dragging a slider in the editor is faster on RAW files: while the slider moves, the picture is
  calculated smaller, and as soon as it rests for a moment, it is calculated in full size, still
  before you let go. With the graphics card switched on, tone, colour and detail sliders also no
  longer send the whole picture to the card at every step.

- Two new frame types: Keyline draws a solid border with a fine line along its inner edge, in a
  colour and width of its own, and 35mm film draws a film strip with sprocket holes, running
  across or upright as you choose.

- Changing a frame responds faster, and so does the analysis picture while objects lie on the
  picture.

- In the compact adjustments mode, extensions appear as a group of their own inside Adjustments
  instead of as a separate tool.

- The groups of extensions remember whether they were open or closed, like the other groups.

- The Extensions tool shows the analysis picture and the clipping warning like the other
  adjustment tools.

- In the viewer and in full screen, the UP and DOWN arrow keys page through the pictures like
  LEFT and RIGHT.

- The search field in the settings stands out with an accent frame, and CTRL+F jumps to it.

- Opening a RAW in the editor right after selecting it no longer waits for the analysis picture
  of the info panel.

- Moving, rotating and resizing several objects at once in the editor runs more smoothly.

- The graphics card speed comparison is remembered instead of being repeated in every session.

- The adjustments panel in the editor is wider by default, so the sliders have a longer track and
  react more finely. It can still be dragged wider at its edge.

- Extensions are now downloaded in the settings, section Extensions, with Download extensions.
  The list shows every extension in the FerrumPix directory with a short description, a sample
  picture, a button to its description and a button that downloads it right away. Extensions you
  have are marked as installed, and a newer version is offered with Update. FerrumPix no longer
  comes with extensions; whoever showed one of the four from 0.9.87 finds it at the top of the
  list.

- Two new extensions by Spoilerhead can be downloaded: a vignette and a graduated filter, both
  of which keep bright highlights.

- Pasting adjustments onto JPG, PNG or WebP pictures can now save a copy next to the original
  instead of overwriting it. The copy is named by the pattern of Save as, for example
  picture_fx.jpg. Overwriting is still offered as the second choice.

- Settings, section Performance, show where the diagnostics log is kept on this system, with
  buttons to open the folder and to show the log. The log window shows errors.log and
  diagnostics.log one below the other; the text can be selected and copied.

### Fixes

- RAW files from older iPhones (6s to 11 Pro Max and the SE models) came out with corners that
  were too bright, because the brightening of the corners stored in the file was applied in full.
  It is now held to a limit, and the corners look as they should.

- RAW files from the Sony A7 V in its new compressed format came out a little too dark, with
  highlights that never reached full white. They now develop as bright as the camera's other
  files.

- With the base brightness matched to the camera model, the Hasselblad X2D II now gets a value of
  its own; it had none after the switch to the newer raw library.

- The sliders of extensions updated the picture only after a pause; now it follows while you drag.

- In trackpad mode, a zoomed in picture in the editor took every click to move the view, so
  objects could not be selected, deselected or created and the Move button had no effect. Now the
  Move button comes first, a click always goes to the tool, and dragging moves the view only in the
  adjustment tools. Selection, masks, text, brushes and the other tools keep their own drag.

- The mouse wheel moved every slider of an extension by 1, so a slider from 0 to 1 went from one
  end to the other in two steps. Sliders with decimals now move by about a hundredth of their
  range.

- The quick preview with SPACE showed the default version of a RAW after a moment instead of the
  variant whose tile was selected, for example the colour picture instead of the black and white
  variant.

- A picture pasted as a layer in the editor looked softer than the photo it was copied from, also
  in the saved picture. It now lies on the photo exactly as copied. The editor also shows
  sharpening in a zoomed out view as strong as the finished picture has it at that size, instead
  of stronger, so the preview tells you what the export gets.

