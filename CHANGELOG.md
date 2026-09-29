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

## 0.9.55

### What's new

- **Remove color fringes by hand.** The lens correction has two new sliders, Red/Cyan and
  Blue/Yellow, for fringes the lens profile leaves behind or for lenses without a profile.

- **Aspect ratios stay put while cropping.** Choosing 16:9, Original or another ratio keeps the
  crop frame in that shape while you drag it, typing a width or height adjusts the other, and
  Free lets it go again. The edge and size values now follow the frame while you drag.

- **Switch tools without the tool bar.** CTRL+TAB and CTRL+SHIFT+TAB step through Adjust,
  Color, Details, Effects, Filters, Transform, Image size, Distort, Selection and Mask in a loop.
  An editor setting adds two buttons for the same at the bottom of the adjustment panel.

- **Scroll bar of the adjustment panel.** A new editor setting keeps it always visible instead of
  showing it only when the pointer is over it. The settings window now always shows its scroll bar.

### Fixes

- Lens correction: removing color fringes at the image corners made them stronger instead of
  weaker. It now works in the right direction and takes most of the fringe away.

- Raw files develop noticeably faster, and the lens correction sliders no longer redevelop the
  picture at every step while you drag them.

- Canon CR3 pictures taken in portrait orientation are shown upright in the viewer, on gallery
  tiles and in the "overwrite file" dialog again instead of lying on their side. That dialog now
  shows both pictures whole instead of cropped.

- Fujifilm raw files taken with an extended dynamic range setting (DR200, DR400) no longer open
  too dark; the exposure starts where the camera meant it to be.

- At 100 percent, moving a lens correction slider no longer jumps back to the middle of the
  picture, so you can watch the effect where you zoomed in.

- Raw files without edits no longer get a color blotch reduction by default. It hardly helped on
  noisy pictures, took away fine color on clean ones and slowed down every change of picture. The
  Color blotches slider in the editor is still there for pictures that need it, and Settings, Raw
  development lets you choose starting values for color noise and color blotches.

- Color and luminance range masks follow their sliders much faster, because the picture is no
  longer recalculated at every step. While you refine the mask of an existing adjustment, it now
  looks at the picture below that adjustment, so the mask no longer shifts with its own effect.

- While Move is switched on in the editor, the pointer shows a hand, so it is clear why the tools
  do not respond to clicks.

- A saved edit that was applied but not saved is offered again the next time the picture is
  opened, instead of the picture silently showing up without it. While such an edit is applied,
  the lens correction explains why it is not available instead of simply disappearing.

- Pasting a copied selection puts it exactly where it was copied from. It used to land a little
  further to the right and down with every paste, so after Select all part of it hung over the
  edge of the picture.

- Painting or erasing on a layer shows up in the history as painted or erased. Every stroke on a
  layer used to be listed as a smudge.

- Brushes and the retouch tools now go up to 2000 pixels instead of 500, for large, soft strokes
  on high-resolution pictures.
