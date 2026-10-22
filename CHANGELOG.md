## Unreleased

### What's new

- **Text stays text in PSD exports.** Plain text now goes into the PSD as a real text layer, so
  the wording, font and size can still be changed in other editors. Text with a shadow, glow,
  outline, gradient or along a path still goes out as an image. Opened again in FerrumPix, the
  text comes back as text in the right place, even after cropping. Text layers from other
  programs now also keep their angle, alignment and letter spacing when you open them as text.

- **Locked layers stay locked in PSD files.** Locking a layer's position or its transparent
  pixels now carries over to the PSD and back.

- **Smaller fixes:** invisible layers no longer turn into empty full-size layers in a PSD export,
  and PSD files open a little faster. The last page of the Windows installer now reads
  "Launch FerrumPix" in English instead of the German wording.

## 0.9.69

### What's new

- **See and find your edited pictures.** A new "Edited" section in the gallery filter shows only
  edited or only unedited pictures, so you can select them all and export, convert or upload
  just those. If you like, a small badge on the tile marks edited pictures, next to the one for
  rejected pictures; switch it on in the settings under "Mark edited photos". In the photo wall
  it only appears while the mouse is over the tile. Stars, labels and keywords alone don't count
  as an edit.

- **Sharper lens correction.** Correcting lens distortion no longer softens fine detail such as
  hair; pictures keep noticeably more of their sharpness. The lens data is updated as well and
  knows a few more lenses.

- **The random collage looks like a pile of real prints.** Each picture now lies on a white
  print with a soft shadow, keeps its own shape instead of being cut square, and the prints are
  scattered and turned across the whole page and beyond its edges, without gaps in between.
  The picture frame setting sets the width of the white border. You choose how big the prints
  are and how much their sizes vary, and with the margin whether the pile stays inside the page
  or runs over its edges.

- **Collages in any size.** Besides the automatic height, a collage can now take an aspect
  ratio such as 3:2, 16:9 or A4, upright or across, or a width and height of your own.

- **Faster, larger previews for collage and printing.** Both dialogs now read each picture only
  once while they are open instead of again after every change, and they grow with the window
  so the preview gets more room.

- **PSD files open more faithfully.** Layers set to Linear Dodge or Soft Light now blend as they
  should, a painted layer mask next to a vector mask is kept, text, shape and smart object layers no
  longer get their edges masked twice, and one unreadable layer no longer turns the whole file
  flat. When a file uses layer effects such as shadows or strokes, FerrumPix asks whether to
  open it as layers without the effects or as a flattened image. In the gallery and viewer,
  files saved without maximum compatibility no longer show a white area, transparent edges no
  longer get a light fringe, and a saved selection is no longer taken for transparency. PSD
  files exported from FerrumPix store their transparent edges the way other programs expect.
  Combining distant or feathered masks now respects the import's memory limit.

- **Exposure on raw files works like in a raw developer.** The exposure slider now acts on the
  raw data before the tone curve, so darkening brings back detail in bright skies instead of just
  greying them, and brightening opens up the shadows more naturally. While you drag, the picture
  follows at once and already looks almost like the result; a moment after you let go, it is
  rebuilt from the raw data. Automatic enhancement chooses the exposure the same way and gives
  the same result however often you press it.

- **Raw files from every camera come out at the right brightness.** On a new installation,
  FerrumPix now adjusts the base brightness to the camera model from the start, so raw files
  from most cameras no longer open noticeably too bright. If you already use FerrumPix, nothing
  changes for you and your edited pictures keep their look; you can switch it on in the settings
  under "Match base brightness to the camera model".

- **Tone sliders recalibrated.** Shadows, whites, highlights and blacks were tuned again to the
  corrected base brightness. The whites slider now has the strength you would expect in both
  directions, highlights are a little gentler and pulling the blacks down works more strongly.
  The shadows slider now also looks at how much of the picture is bright: next to a bright sky
  or a light wall, dark parts open up further, as in a raw developer. In dark pictures, such as
  concerts or night shots, the highlights slider now pulls bright areas down much more firmly.
  Detail in bright skies and clouds is now always brought back from the raw data, so the
  checkbox for it in the light panel is gone. On raw files, pulling the highlights down now
  reaches into that detail as well: bright skies, clouds and burnt out areas come down about as
  far as in a raw developer, where before the slider stopped at half the way. Clouds and bright
  skies also keep their texture when you pull the highlights down, instead of turning flat and
  milky, and colours there no longer fade. Lifting the shadows keeps their colour as well.
  All pictures now share one calculation for these sliders, so pictures you edited in earlier
  versions can look slightly different.
  
### Fixes
  
- **Smaller fixes.** The text cursor sits in the right place and at the right size, also while
  you resize a text box. The curve editor draws the luminance curve the way it is applied. A
  picture you rename stays in the open burst. Choosing a folder in "Save as", in searches and in
  the settings starts in the folder already entered instead of your home folder. In the filter
  menu, bursts, rejected and edited pictures now come after the colour labels, and the people
  and places lists are as tall as the keyword list.

