## 0.9.53

### What's new

- **Denoise strength from the picture.** The AI denoiser in the editor can measure how noisy a
  photo is and set a matching strength. Applying a filter to a selection can now denoise as well,
  with a fixed strength or measured for each picture, and pictures that are already clean are left
  as they are. Choose *No filter* there to only denoise. *Export to* has its own denoise section.

- **Browse the filmstrip in the editor.** Clicking a picture in the filmstrip below the editor
  only selects it, so you can scroll and look around freely. Double-click, ENTER or the context
  menu opens it; the context menu can also insert it as a layer. Dragging a picture onto the photo
  now shows where it will land.

- **Export button in the gallery.** With pictures selected, an *Export* button appears next to the
  menu at the bottom of the gallery.

### Fixes

- The selection circle and every action on a gallery tile have a larger click target. The timeline
  only takes clicks on its narrow edge, and clicking an inactive button in the footer no longer
  starts moving the window.

- Smoothed brush and mask strokes now end where you lift the pen or mouse instead of stopping
  short. A pen without a pressure sensor paints at the full brush size again, and the brush preview
  no longer shows pressure for brush types that do not use it.

- Undo and redo no longer select a different text when the selected one was removed, changing a
  large selection can no longer close the app, and saved searches keep files whose names differ
  only in upper and lower case apart.

- Saved searches show their pictures right away, even from folders you have never opened in the
  gallery, and look for new pictures on network shares much faster.

- Dragging a curve point, colour wheel or slider with a graphics tablet pen no longer scrolls the
  whole panel along with it.

- Favourites can be given their own name in the list without renaming the folder, album or search
  behind them. Their menu no longer offers to rename or delete the folder or album itself.
