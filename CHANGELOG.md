## Unreleased

### What's new

- **Denoise strength from the picture.** The AI denoiser in the editor can measure how noisy a
  photo is and set a matching strength. Applying a filter to a selection can now denoise as well,
  with a fixed strength or measured for each picture, and pictures that are already clean are left
  as they are. Choose *No filter* there to only denoise.

- **Browse the filmstrip in the editor.** Clicking a picture in the filmstrip below the editor
  only selects it, so you can scroll and look around freely. Double-click, ENTER or the context
  menu opens it; the context menu can also insert it as a layer. Dragging a picture onto the photo
  now shows where it will land.

### Fixes

- Smoothed brush and mask strokes now end where you lift the pen or mouse instead of stopping
  short. A pen without a pressure sensor paints at the full brush size again, and the brush preview
  no longer shows pressure for brush types that do not use it.

- Undo and redo no longer select a different text when the selected one was removed, changing a
  large selection can no longer close the app, and saved searches keep files whose names differ
  only in upper and lower case apart.

## FerrumPix 0.9.52

### What's new

- **Align text.** Text with several lines can be set left, centred, right or justified.

- **Finer shadow offsets.** The offset of a text shadow moves in tenths; hold SHIFT in the value
  field to step in whole numbers.

- **Colours that stand out.** New text, brush strokes and the outlines of shapes and symbols start
  in a colour that contrasts with the picture's background, until you pick one yourself.

- **More cameras for lens correction.** The lens data is up to date again and knows a few more
  cameras, among them the Nikon Z5IIC, Canon EOS R8 Mark II and Panasonic TZ300.

### Fixes

- The frame around a selected text sits tightly on the letters, so text lines up cleanly with
  guides and other objects. It stays right after resizing, undo and redo.

- Moving a text layer no longer cuts off parts of its shadow.

- Thick outlines on shapes and symbols are no longer cut off at the edge while the object is
  selected.

- Moving pictures between folders inside the gallery keeps the normal pointer and shows clearly
  which folder will receive them. ESC cancels a move, and a move interrupted by switching windows
  no longer drops the pictures on the next folder you click. Pictures can be dragged out of the
  gallery into a file manager or another program again.

- Saving an edited FPX file works again, and saving it in place no longer asks to save once more
  when you go back to the gallery.

- Watched folders on a network share are checked several times faster at startup when nothing
  has changed.

- Saved searches show their pictures right away, even from folders you have never opened in the
  gallery, and look for new pictures on network shares much faster.

- Dragging a curve point, colour wheel or slider with a graphics tablet pen no longer scrolls the
  whole panel along with it.

- Favourites can be given their own name in the list without renaming the folder, album or search
  behind them. Their menu no longer offers to rename or delete the folder or album itself.
