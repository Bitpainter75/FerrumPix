## Unreleased

### Changes

- The gallery can show the pictures from all subfolders of a folder as well. A new button left of
  the search field switches this on and off; searching and filtering then cover the subfolders too.

- FerrumPix can be used in a web browser. Started with `--browser`, it opens no window and shows
  itself at `http://127.0.0.1:14500` instead; with `--browser-bind`, `--browser-user` and
  `--browser-password` it can be reached from another device behind a sign-in. This is a first
  version: copy and paste stay inside FerrumPix, and videos play without sound.

- On some newer Linux systems FerrumPix ended right at startup, before a window appeared. It starts
  there now.

- When deleting is refused because the pictures are outside the home folder, the message now says
  where this can be allowed in the settings.

- The healing brush continues lines that run through the repaired spot, such as a treeline or the
  horizon, instead of pulling the ground up into the sky or leaving a bright patch in the trees.
  Where no fitting part of the picture is found, it repairs as before.

- A new setting under Editor decides where the home button in the editor leads: back to where the
  editor was opened from, as before, or always to the gallery. ESC still goes back one step.

- The Metadata menu can now remove the AI keywords or the people of the selected pictures, and it
  offers Find faces for the selection as well. Removed keywords and faces do not come back on their
  own; Analyse AI keywords again and Find faces bring them back on request.

- RAW, HEIC and JPEG XL now always open with the libraries that come with FerrumPix, also where the
  system has its own. Every computer develops a RAW the same way, and cameras the bundled RAW
  library knows work everywhere.

- When zoomed in, the info panel of the viewer, the full screen view and the editor shows a
  navigator at the bottom: the picture small, with a frame around the part you are looking at. Drag
  the frame, or click beside it, to look at another part.

- Raw files from cameras that let you choose the aspect ratio, such as 3:2, 16:9 or 1:1 on Panasonic
  cameras, open in the editor in the format chosen on the camera. A new switch under Lens correction
  shows the whole sensor again. Pictures you edited before keep the whole sensor.

- Auto is more careful with pictures taken at high ISO: it brightens them less and keeps more of
  the colour of the light, so the mood of a dim indoor scene stays and the noise stays down.

### Fixes



