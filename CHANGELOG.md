## Unreleased

### Changes

- The viewer can show a frame around every detected face with the person's name below it,
  switched on and off with a new button in the footer. Confirmed names, suggestions and faces
  without a name look different, and the frames follow zoom, rotation and cropping.

- The settings link to the documentation: an entry below the homepage opens the manual in the
  browser.

- Exported JPEG files now carry a small sRGB colour profile, so every program shows their colours
  the same way, also when the picture is saved without metadata.

- Depth mask, depth blur and depth fog use a new, much more accurate depth model. It separates
  thin and close things such as fences, branches, ropes and single people from the background,
  where the old one often saw a flat surface. The model file is new: the settings offer it as an
  update, and the depth tools need it.

- Depth blur and the depth mask have an eyedropper: click the subject in the picture and the sharp
  range or the selected distance is placed around it. The choice of an aperture shape for the depth
  blur is gone, the blur is always round.

### Fixes

- Lighting with depth looks cleaner: the relief no longer draws a dark rim around every object or
  contour lines across flat surfaces, light and shadow end at the outline of an object instead of
  a few pixels beside it, and cast shadows keep the shape of what throws them. A new slider sets
  how softly the edge of a cast shadow fades.

- Depth blur no longer lets the colour of the sharp subject bleed into the blurred background
  around it.

- Depth fog leaves the nearest part of the picture clear when the foreground slider is at zero.

- Depth fog, depth blur and the depth mask leave a narrower fringe around objects.

- The white balance picker ignores points where a colour channel is blown out, so a click near
  bright highlights no longer pulls the result towards the colour of the clipped channel.

- Videos in the viewer: two quick clicks on the seek bar no longer jump back to the start, the
  handle no longer slips away under the pointer while dragging, the play button shows the right
  state after switching to another video, and the mouse wheel on the seek bar really jumps.

- When the graphics card runs out of memory during the depth map, denoising, object removal or
  upscaling, for example because another program is using it, FerrumPix now continues on the
  processor instead of only reporting that it did not work.

