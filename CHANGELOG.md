## FerrumPix 0.9.45

### What's new

- **Places from a GPS track.** Select photos, point FerrumPix at a GPX file, and every one of them
  gets the spot the track was at when it was taken. You set how far the camera clock is from the
  recording; the dialog counts how many photos that reaches before anything is written.

- **Noise reduction follows the noise.** A new default method measures how noisy the picture is at
  each brightness, smooths quiet areas gently, and also takes out the coarse, blotchy noise of high
  ISO shots while hair and fabric keep their texture. Anything you edited before opens as you left it. RAW files you have
  not edited yet start with a moderate setting against coarse colour blotches.

- **Sharpening that reverses the blur.** A second sharpening method brings fur, feathers and hair
  back into focus without the coloured fringes of the unsharp mask, and lifts noise far less. The
  unsharp mask stays the default.

- The viewer can develop every RAW, edited or not. A new setting under RAW development shows your
  own development in the viewer and in fullscreen instead of the preview the camera put in the
  file. It is off to begin with, because a picture then takes longer to appear.

### Fixes

- The tone curve behaves the way you draw it. A straight curve stays straight, wherever you put
  your points, rising points give a rising curve, and steep points no longer throw the curve past
  them into areas of pure black or white. Presets with a tone curve can look slightly different
  than before.

- Straightening with *Crop canvas automatically* keeps its crop after Apply. The crop frame jumped
  back to the whole picture, leaving the tool asked to apply again, and doing so brought the empty
  wedges back.

- Smaller things: the red mask overlay no longer stays on top of the picture when you switch from
  the adjustment sliders to the brush with a mask layer selected, and picking a path in the layers
  panel opens the Path tool again, because a path has nothing to adjust or select and its points
  are only reachable there.
