## Unreleased

### Changes

- Dragging a slider in the editor is faster on RAW files: while the slider moves, the picture is
  calculated smaller, and as soon as it rests for a moment, it is calculated in full size, still
  before you let go. With the graphics card switched on, tone, colour and detail sliders also no
  longer send the whole picture to the card at every step. The sliders of extensions update the
  picture while you drag them instead of only after a pause.

- Two new frame types: Keyline draws a solid border with a fine line along its inner edge, in a
  colour and width of its own, and 35mm film draws a film strip with sprocket holes, running
  across or upright as you choose.

- The search field in the settings stands out with an accent frame, and CTRL+F jumps to it.

- Opening a RAW in the editor right after selecting it no longer waits for the analysis picture
  of the info panel, and dragging objects in the editor runs more smoothly. The graphics card
  speed comparison is remembered instead of being repeated in every session.

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

