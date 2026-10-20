## 0.9.69

### What's new

- **Exposure on raw files works like in a raw developer.** The exposure slider now acts on the
  raw data before the tone curve, so darkening brings back detail in bright skies instead of just
  greying them, and brightening opens up the shadows more naturally. While you drag, the picture
  follows at once and already looks almost like the result; a moment after you let go, it is
  rebuilt from the raw data. Automatic enhancement chooses the exposure the same way and gives
  the same result however often you press it. Pictures you edited before keep their look.

- **Raw files from every camera come out at the right brightness.** On a new installation,
  FerrumPix now adjusts the base brightness to the camera model from the start, so raw files
  from most cameras no longer open noticeably too bright. If you already use FerrumPix, nothing
  changes for you and your edited pictures keep their look; you can switch it on in the settings
  under "Match base brightness to the camera model".

- **Tone sliders recalibrated.** Shadows, whites, highlights and blacks were tuned again to the
  corrected base brightness. The whites slider now has the strength you would expect in both
  directions, highlights are a little gentler and pulling the blacks down works more strongly.
  The shadows slider now also looks at how much of the picture is bright: next to a bright sky
  or a light wall, dark parts open up further, as in a raw developer.
  Pictures you edited with 0.9.68 can look slightly different in these four areas; edits from
  earlier versions keep their look.

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

- **PSD files open more faithfully.** Layers set to Linear Dodge now blend as they should, a
  painted layer mask next to a vector mask is kept, text, shape and smart object layers no
  longer get their edges masked twice, and one unreadable layer no longer turns the whole file
  flat. When a file uses layer effects such as shadows or strokes, FerrumPix asks whether to
  open it as layers without the effects or as a flattened image. In the gallery and viewer,
  files saved without maximum compatibility no longer show a white area, transparent edges no
  longer get a light fringe, and a saved selection is no longer taken for transparency. PSD
  files exported from FerrumPix store their transparent edges the way other programs expect.
  Combining distant or feathered masks now respects the import's memory limit.

