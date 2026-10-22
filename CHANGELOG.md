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
  as you hold still. Histogram and tone curve are now updated in the background, and highlights
  and shadows are a little quicker to calculate.

- **Smaller fixes:** invisible layers no longer turn into empty full-size layers in a PSD export,
  and PSD files open a little faster. The last page of the Windows installer now reads
  "Launch FerrumPix" in English instead of the German wording.

### Fixes

- **Collage layout buttons:** the selected layout is highlighted again. Before, "Grid" stayed
  highlighted, and with "Hero" the hint and the choice of the hero position did not appear.
  
- **Batch rename:** the placeholder buttons now sit in a tidy row under the name pattern, as in
  the export dialog.
  
- **Histogram, waveform and RGB parade in the editor** now show the whole picture including its
  layers and objects. With layered PSD files they stayed empty.
  
- **Faster browsing of developed RAWs in the viewer:** when you skip quickly through RAW files with
  development switched on, the viewer no longer develops the pictures you have already passed.
  
- **Vibrance protects skin tones:** raising Vibrance now holds back on reds, oranges and skin
  while lifting greens, blues and skies more, and it no longer brightens colours as it saturates
  them. Before, it acted almost like Saturation.
  
- **ISO on Nikon JPGs:** pictures taken at the extended Hi ISO settings now show their ISO in the
  gallery and the info panel. Before, only the NEF files had it.
  
- **Opening PSD files:** empty layers are kept as empty paint layers with their name and mask
  instead of disappearing. Text that is skewed or mirrored now comes in as an image, so it keeps
  its look instead of turning into straight text.
  
- **Tone curve on a layer:** when you adjust a layer, the histogram behind the curve now shows
  that layer instead of the background, so it no longer stays empty with layered PSD files.

