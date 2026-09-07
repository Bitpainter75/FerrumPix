## FerrumPix 0.9.40

### What's new

- **Straightening can trim the empty corners away.** Switch on "crop canvas automatically" in the
  transform tool and levelling a horizon trims the canvas to the largest rectangle that still lies
  fully inside the tilted picture, so no empty wedges are left at the corners. It keeps the shape of
  the picture, and the trimmed strip is not gone: open the transform tool again and the whole tilted
  picture is back under the frame, ready to be pulled out. The switch above it does the opposite and
  enlarges the canvas instead; both remember how you left them.

- **More crop formats to pick from.** Next to the familiar ones there is now 5:4, and the same
  formats upright: 3:4, 2:3 and 4:5.

- **The transform tool asks before it drops your work.** Crop and orientation only take effect when
  you press Apply. Leaving the tool with either of them still open now asks whether to apply it
  first, instead of quietly putting the picture back the way it was.

### Fixes

- The straighten slider follows the mouse again. While you drag it, turn the wheel over it or step
  it in the number box, the preview is computed from a smaller copy of the picture, the way the
  exposure and colour sliders have done for a while; the full resolution comes back as soon as you
  stop.

- The batch dialogs no longer print two rows on top of each other: with "overwrite the originals"
  switched on, the JPG quality row and the copyright field shared the same line. The dialogs are
  also a little wider, so the size presets fit on one row again.

