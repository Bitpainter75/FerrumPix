## 0.9.71

### What's new

- **Fujifilm lens distortion from the camera:** when the lens database has no profile for your
  Fujifilm lens, FerrumPix now corrects its distortion with the values the camera stores in the
  RAF file, as it already did for Sony, Olympus and Panasonic.

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

