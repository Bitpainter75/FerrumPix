## Unreleased

### Changes

- The viewer can show a frame around every detected face with the person's name below it,
  switched on and off with a new button in the footer. Confirmed names, suggestions and faces
  without a name look different, and the frames follow zoom, rotation and cropping.

- Exported JPEG files now carry a small sRGB colour profile, so every program shows their colours
  the same way, also when the picture is saved without metadata.

### Fixes

- The white balance picker ignores points where a colour channel is blown out, so a click near
  bright highlights no longer pulls the result towards the colour of the clipped channel.

