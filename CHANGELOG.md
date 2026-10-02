## 0.9.59

### What's new

- **Hardness for the clone stamp, healing brush and smudge.** A new hardness slider sets how soft
  or hard the edge of these tools is, just like for the paint brush. It starts where the edge has
  always been, so nothing changes until you move it.

- **Brush sizes are remembered.** The brush, eraser, smudge, healing brush and clone stamp each
  keep their size and hardness when you switch tools, open another picture or restart the app.

- **Histogram, waveform and RGB parade show what you see.** In the gallery and viewer they now
  describe the picture as it is displayed, for RAW files including your edits, the way Lightroom
  does. They also appear much sooner, because the file no longer has to be read a second time.

- **Straighten with a line.** In the Rotate group, press "Draw a line" and drag along an edge in
  the picture that should be level or upright; the picture turns to match. The angle can now be
  set to a hundredth of a degree in the number field, and automatic straightening shows that it
  is working instead of seeming to do nothing.

- **Search in the settings.** A search field above the list of sections finds a setting by any
  word of its name, its description or its tooltip, in every language, instead of scrolling
  through the whole page.

- **Phone photos in HEIC show up in the gallery right away.** Each tile first shows the small
  preview stored in the file and turns sharp a moment later.

### Fixes

- Black and white film scans saved as DNG by VueScan now open. Before, they did not show up at all.

- A copy made with Save As in the same folder now shows up right away when browsing with the
  arrow keys in the viewer, without a detour through the gallery.

- The selection frame of a rotated text now stays tight around the letters. Before, it grew
  taller while rotating and stood off the text at the top and bottom.

- Right after starting, the first turn of the mouse wheel in the gallery no longer jumps straight
  to the end of the list. The same could happen in other scrolling lists and panels.

- JPEG XL, HEIC, AVIF, TIFF and PSD files open many times faster and use less memory, in the
  viewer as well as in thumbnails and the editor. A folder full of JPEG XL photos could keep the
  viewer waiting for over a minute.

- Faster in the editor: saving a project, turning a selection into a layer, filling, rasterizing
  and merging layers now take about half the time or less, with the same lossless quality.

- No more pause in the editor on the first click with the color picker, when starting a warp
  preview, when opening the full screen view or when pasting a picture from the clipboard.

- Opening a photo from the viewer in the editor reuses the picture the viewer already loaded
  instead of reading the file again. This is most noticeable with HEIC photos from phones. The
  editor always works on the full resolution, also for RAW files.

- Going back from the editor to the viewer without saving no longer reads the picture again.

- The first switch to the editor after starting no longer stutters for a few seconds, and
  opening a photo in the editor no longer freezes the window for a moment.

