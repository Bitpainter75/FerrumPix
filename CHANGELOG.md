## 0.9.71

### What's new

- **Fujifilm lens corrections from the camera:** when the lens database has no profile for your
  Fujifilm lens, FerrumPix now corrects its distortion and vignetting with the values the camera
  stores in the RAF file, as it already did for distortion with Sony, Olympus and Panasonic.

- **More processor cores for the models:** a new setting under Performance decides how many
  processor cores denoising and the other models may use. By default it is still half of them, so
  the app stays responsive; more cores make the models faster when they run on the processor.

### Fixes

- **Less memory over long sessions:** FerrumPix no longer keeps the full metadata of every
  picture it has ever read. With large watched folders this used to add up to a lot of memory.

- **When deleting fails, the picture stays:** if a picture cannot be deleted, for example because
  the trash is not available, the viewer, the editor and the search results keep showing it
  instead of acting as if it were gone.

- **Graphics card on macOS:** the graphics card option now works on Macs as well. Before, it
  always fell back to the processor there, with an untranslated reason in the settings. When the
  card cannot be used, the settings now give the reason in your language.

- **Phone RAWs no longer have dark corners:** DNG files from phones such as Google Pixel, Xiaomi
  and Nokia or Microsoft Lumia carry a correction for the lens's shading. FerrumPix now applies it,
  so their corners are no longer up to two stops too dark.

- **Files that differ only in upper and lower case:** on Linux, IMG.JPG and IMG.jpg
  in the same folder are now always treated as two pictures. Before, deleting both removed only
  one, the gallery could fail to show new ones, and the viewer and editor could jump to or drop
  the wrong one.

- **Faster browsing of RAW files in the viewer:** when the viewer develops RAW files, moving on
  to the next picture now stops the development of the previous one instead of waiting for it to
  finish.

- **Closed eye in the adjustment groups easier to see:** while a group is hidden for comparison,
  its closed eye now uses the text colour, which stands out on every theme, including the grey
  ones.

- **Film negatives without a colour cast from grain:** on grainy colour negatives, the grain of
  the scan could decide the inversion and tint the whole picture, more so on a large monitor.
  FerrumPix now measures the film so that the grain no longer counts, and the same scan turns out
  the same on every screen.

- **Readable with the yellow accent:** text and icons on accent coloured areas were white on
  yellow, for example on the selected layer, the badges on gallery tiles, the drop hints and the
  tick in checkboxes. They now turn dark whenever white would not stand out.

- **Soft Light blending corrected:** over dark areas, layers and PSD files in Soft Light came out
  too light. Pictures that use Soft Light can look a little darker there than before.

- **More LUT files work:** .cube files with a 1D curve in front of the 3D table, pure 1D LUTs and
  LUTs with their own input range used to have no effect at all. They are now applied as the file
  describes.

- **XMP presets with the profile written in another form:** the profile and its amount were lost
  when a preset stored them as separate entries instead of attributes. Both forms are now read.

- **Tooltips in the adjustment groups translated:** the tooltips of the compare and reset buttons
  in the group headers sometimes appeared in German, for example in Colour, Colour mixer and
  Colour grading.

