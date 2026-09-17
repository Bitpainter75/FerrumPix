## Unreleased

### Fixes

- Projects with several masked adjustments open faster, most of all when the masks cover only part
  of a straightened or rotated picture.

- Selecting a mask layer on a straightened, rotated or cropped picture shows its red overlay
  almost at once instead of after a second or more.

- The depth blur is calculated about three times faster, and the healing brush works a little
  faster.

## FerrumPix 0.9.45

### What's new

- **Places from a GPS track.** Select photos, point FerrumPix at a GPX file, and every one of them
  gets the spot the track was at when it was taken. You set how far the camera clock is from the
  recording; the dialog counts how many photos that reaches before anything is written.

- **Noise reduction follows the noise.** A new default method measures how noisy the picture is at
  each brightness, smooths quiet areas gently and also takes out the coarse, blotchy noise of high
  ISO shots, while hair and fabric keep their texture. Anything you edited before opens as you left
  it. RAW files you have not edited yet start with a moderate setting against colour blotches.

- **Sharpening that reverses the blur.** A second sharpening method brings fur, feathers and hair
  back into focus without the coloured fringes of the unsharp mask, and lifts noise far less. The
  unsharp mask stays the default.

- **Your own RAW development everywhere.** A new setting under RAW development shows your own
  development instead of the camera's preview for every RAW, edited or not: in the viewer, in
  fullscreen and in the quick preview of the gallery. It is off to begin with, because a picture
  then takes longer to appear.

- **Back to the original in one step.** *Reset all edits* in the editor clears sliders, crop,
  retouching, masks and layers at once. The original file stays untouched, and your saved edits
  are only replaced once you save again.

- **Name edited copies your way.** *Save as* suggests a name from a template you set in the editor
  settings, `{name}_fx` to begin with. It knows the same placeholders as Batch rename.

- **Dates the way you read them.** A new setting picks the date format, independent of the
  language: as on your system, or day before month, month before day, or year first.

- **More useful photo facts.** The info panel shows exposure compensation, and the file date only
  appears when it differs from the date the photo was taken.

- **Better grouping and comparison.** Grouping by ISO gives each ISO value its own section, and
  pinning a photo for comparison puts the next photo beside it right away.

- **Tidy preset lists in one click.** A small button next to *Load folder* empties the XMP or LUT
  list in the editor. The preset files themselves stay where they are.

- **Clarity that works like clarity.** The slider now lifts contrast in the midtones across a wide
  area, without light or dark rims along strong edges, and brings up far less noise. Before, it
  acted like a second sharpening. Edits with clarity look somewhat different than before.

- **Paste pictures straight from the clipboard.** A screenshot or an image copied in the browser
  becomes a new layer in the editor, not only image files. What you copy in the editor now also
  goes to the clipboard as a picture, so it can still be pasted later and in other programs.

- **Black and white point on the tone curve.** The end points of the curve can now be dragged
  inward as well, on every channel, so you can set the black and white point or tone colours per
  channel.

- **JPEG XL, reading and saving.** Photos in JPEG XL show up in the gallery and open in the
  viewer and the editor, with their colour profile, rotation and capture data. You can save as
  JPEG XL wherever you can choose a format, with the same quality setting as JPEG; 100 means
  lossless. When converting JPEG files to JPEG XL, they can be repacked without any loss: about a
  fifth smaller, and the original JPEG can be restored exactly. The Windows downloads bring
  everything along, on Linux the packages recommend libjxl.
  
  
### Fixes

- The tone curve behaves the way you draw it. A straight curve stays straight, rising points give
  a rising curve, and steep points no longer throw the curve into areas of pure black or white.
  Presets with a tone curve can look slightly different than before.

- Straightening with *Crop canvas automatically* keeps its crop after Apply, instead of bringing
  the empty wedges back.

- Smaller things: the red mask overlay no longer stays on the picture when you switch to the brush
  with a mask layer selected, picking a path in the layers panel opens the Path tool again, and on
  macOS a missing LibRaw installation is pointed out.

- Upscaling in the editor, and applying saved edits that need a model, show the progress display
  with its cancel button. The picture stays locked while they run, so no change made in the
  meantime is lost.

- A layer mask now follows its layer while you drag, scale or rotate it, not only after you let
  go.

- Erasing on an image layer removes the pixels for good. Before, the erased spot flashed back
  while moving the layer and stayed behind as a hole when the layer was moved elsewhere.

- Copying a selection taken from a layer copies only that layer. Transparent and see-through parts
  stay transparent instead of showing the photo underneath.
  
- HEIF photos with the endings .heif and .hif, as some Fujifilm and Canon cameras save them, now
  appear in the gallery.

- The channel switcher of the tone curve now also marks a channel whose end point was moved.

- Lens correction knows a few more cameras and lenses, and the bundled HEIC decoder on Windows is
  up to date.

- A cropped or straightened project opens in the editor as it was saved, instead of showing the
  whole uncropped picture for a moment first.
