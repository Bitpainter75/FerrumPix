## 0.9.70

### What's new

- **Straight to the editor from the "edited" badge.** A click on the badge of an edited picture
  in the gallery opens it in the editor.

- **Text stays text in PSD exports.** Plain text now goes into the PSD as a real text layer, so
  the wording, font and size can still be changed in other editors. Text with a shadow, glow,
  outline, gradient or along a path still goes out as an image. Opened again in FerrumPix, the
  text comes back as text in the right place, even after cropping. Text layers from other
  programs now also keep their angle, alignment and letter spacing when you open them as text.

- **Locked layers stay locked in PSD files.** Locking a layer's position or its transparent
  pixels now carries over to the PSD and back, also for the bottom layer.

- **Smoother sliders in the editor.** The short freeze when you first grab a slider on a large
  picture is gone, and the picture follows the slider more closely. When zoomed in, the sharp
  detail view no longer keeps recalculating in the background while you drag; it appears as soon
  as you hold still. Histogram and tone curve are now updated in the background, and dragging
  highlights, shadows, whites, blacks or vibrance is noticeably quicker.

- **Graphics card for image editing (try it out).** A new switch under Settings, Performance lets
  the graphics card calculate colour and tones as well as all detail and effect tools such as
  noise reduction, sharpening, clarity, haze, blur, grain and vignette, in the preview as well as
  for export and batch processing. Sliders react noticeably faster on large pictures, and
  sharpening with the deconvolution method no longer takes seconds. It is off by default; when you
  turn it on, FerrumPix first checks that the card calculates the same picture as the processor
  and otherwise stays with the processor. Curves, colour grading, point colour and LUTs are
  included. Without a real graphics card, for example in a virtual machine, with a graphics card
  that turns out slower than the processor, and after the graphics driver hung or crashed,
  FerrumPix also stays with the processor.

- **Immich and Nextcloud photos on the map.** The map view now also shows pictures from a
  connected Immich or Nextcloud server, using the location the server knows for them. For
  Nextcloud this needs version 28 or newer.

- **Developed RAWs are marked in the viewer too.** As in the editor, the file name at the top of
  the window turns to the accent colour when the viewer shows a RAW developed by FerrumPix rather
  than the camera's built-in preview.

- **Smaller fixes:** invisible layers no longer turn into empty full-size layers in a PSD export,
  and PSD files open a little faster. The last page of the Windows installer now reads
  "Launch FerrumPix" in English instead of the German wording.

### Fixes

- **White stays white in the colour mixer:** lowering the luminance of a colour such as yellow
  or green no longer tints bright clouds, white walls or blown highlights.

- **Collage layout buttons:** the selected layout is highlighted again. Before, "Grid" stayed
  highlighted, and with "Hero" the hint and the choice of the hero position did not appear.
  
- **Batch rename:** the placeholder buttons now sit in a tidy row under the name pattern, as in
  the export dialog.
  
- **Histogram, waveform and RGB parade in the editor** now show the whole picture including its
  layers and objects. With layered PSD files they stayed empty.
  
- **Faster browsing of developed RAWs in the viewer:** when you skip quickly through RAW files with
  development switched on, the viewer no longer develops the pictures you have already passed.
  
- **RAWs open faster in the editor from the viewer:** while a RAW stays on screen in the viewer,
  FerrumPix prepares it in the background, so switching to the editor takes noticeably less time.
  
- **Vibrance protects skin tones:** raising Vibrance now holds back on reds, oranges and skin
  while lifting greens, blues and skies more, and it no longer brightens colours as it saturates
  them. Before, it acted almost like Saturation. It is also gentler overall now, especially on
  faces, so skin no longer turns orange at high settings.
  
- **ISO on Nikon JPGs:** pictures taken at the extended Hi ISO settings now show their ISO in the
  gallery and the info panel. Before, only the NEF files had it.
  
- **Opening PSD files:** empty layers are kept as empty paint layers with their name and mask
  instead of disappearing. Text that is skewed or mirrored now comes in as an image, so it keeps
  its look instead of turning into straight text.
  
- **Tone curve on a layer:** when you adjust a layer, the histogram behind the curve now shows
  that layer instead of the background, so it no longer stays empty with layered PSD files.
  
- **Face names from other photo programs:** named faces that another program stored in an XMP
  sidecar file now come into FerrumPix with their name and position. Before, files in the common
  notation of these programs were skipped.
  
- **Collages with many pictures react much faster:** picture objects are kept in memory instead
  of being read from disk every time, and objects with blend modes like Soft Light are drawn
  several times faster. Moving an object in a collage of 30 pictures no longer takes seconds.
  
- **Clean picture edges:** noise reduction, colour noise reduction, structure, glow, the soft
  filter and sharpening no longer darken the border of the picture or make it slightly
  transparent. With colour noise reduction the colours at the border no longer shift either.

