## FerrumPix 0.9.48

### What's new

- **Highlights back out of the raw data.** A blown sky often still has detail in one channel while
  another has given up, and until now that detail was thrown away before you ever saw it. The new
  tick in the Light panel brings it back: on one test shot nine out of ten blown pixels carry
  drawing again. It is off by default, it only appears for raw files, and switching it develops
  the file again, so it takes a moment.

  Raw files look a hair different now even with the tick off, and only in one place: where a
  colour channel used to run into the stop on its way through, it now keeps its real value. That
  is a hundredth of a percent of the picture, and it is the truer colour.

- **An eyedropper for the white balance.** Click a spot that should be neutral grey and the white
  balance follows it. It reads the spot from the picture as the camera handed it over, so it lands
  right the first time no matter what else you have already set, and the white balance then says
  "Custom" - a word that until now did nothing at all.

- **Opacity for the mask brush.** A stroke no longer has to cover fully: set the brush to 40 and it
  paints the mask to 40 percent, and subtracting takes away just as little. The setting is the
  coverage you get, so going over the same spot again does not deepen it - a stronger brush does.

- **Window buttons the way your desktop has them.** Close, maximize and minimize now take their
  side and their order from your desktop, so they sit where you are used to them, on the left as
  well. They stay one group, and buttons your desktop leaves out are still there - they are the
  only way to minimize from the title bar. The settings let you pin them to one side.

### Fixes

- Opening a TIFF, HEIF or JPEG XL in the editor shows that it is loading. Until now the filmstrip
  jumped to the new picture while the stage still held the old one and nothing said why, so it
  looked as if two pictures were picked at once. The filmstrip also marks the new picture the
  moment you click it instead of when the picture finally arrives.

- DNG files are developed like every other raw file again. They were being mistaken for finished
  pictures because of the preview stored inside them, and skipped the whole development stage.

- The slider for the application scaling in the settings is wide enough now to reach every
  percent. It was so short that a good many values could not be set by dragging at all.

- RAW files now develop to the same brightness whatever happens to be in the picture. The RAW
  library was quietly reading the white point off the brightest spot of each shot, so two photos
  taken with the same settings could come out differently bright - on some cameras by a tenth of a
  stop.

- Switching between gallery, viewer and editor is now immediate. Every switch used to rebuild the
  whole view, which took about a third of a second on the way to the gallery and well over half a
  second on the way to the editor. The views are kept now, so the switch takes a few milliseconds.
  The editor is still only built the first time you go there.

- Cutting out a selection now gives you what actually disappears. Cut from the photo and a layer
  lying over it came along in the cutout, so pasting it back put that part in twice. With an image
  layer marked, CTRL+X did nothing at all, and dragging inside the marching ants moved the layer
  instead of the selection. CTRL+C follows the same rule as cutting: while a selection is running
  it takes that cutout, and only without one does it copy the marked layer.
