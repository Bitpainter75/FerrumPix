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

