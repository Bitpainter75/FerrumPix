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

### What's new

- **Old raw formats keep their date and camera.** Pictures from Canon's early CRW files, Minolta
  MRW, the plain .RAW of Leica, Panasonic and Kodak and Leaf's MOS arrived without a shooting date,
  without a camera name and without dimensions, so they dropped out of the timeline, out of sorting
  and out of every filter. Those details are read from the raw file itself now, together with ISO,
  aperture, shutter speed and focal length. Anything a picture already carries stays untouched.

### Fixes

- Old Canon CRW files no longer show grey noise in the viewer and the gallery. Those files store
  the sensor data in a way that looked like a preview image, and being the largest picture inside
  the file, it won. The real preview is used now, and where there is none, the picture is developed.

- Fujifilm RAF files open as the picture again. The lens corrections were sized against the small
  preview image stored inside the file instead of the photo itself, so they pulled the whole frame
  into a ball with smeared, washed out edges and green corners. The same mistake made the
  corrections come out too strong on gallery thumbnails of every raw format.

- The straighten slider follows the mouse again. While you drag it, turn the wheel over it or step
  it in the number box, the preview is computed from a smaller copy of the picture, the way the
  exposure and colour sliders have done for a while; the full resolution comes back as soon as you
  stop.

- The batch dialogs no longer print two rows on top of each other: with "overwrite the originals"
  switched on, the JPG quality row and the copyright field shared the same line. The dialogs are
  also a little wider, so the size presets fit on one row again.

