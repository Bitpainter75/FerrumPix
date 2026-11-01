## 0.9.75

### Changes

- **New tool: Lighting.** Place lights and shadows anywhere on the picture and aim them right
  there: point light, spot with direction and cone, soft area light, grazing light and sun rays,
  each with brightness, reach, softness, color temperature or a free color and an optional halo.
  Light types like candle, bulb, LED, street lamp, floodlight, flashlight, evening sun, neon or
  shadow give a quick start. With the depth model the light can follow the depth of the scene. The
  lights stay editable at any time and are saved with the picture's edit. Lights now work like light
  at the time of the shot, before tone and colour, so bright light rolls off softly instead of
  clipping. A light can stand higher or lower above the scene, an ambient control darkens or
  brightens the scene around the lights, and with the depth model light follows the shape of
  surfaces and can cast shadows behind objects.

- **Faster import from large cards:** the capture days and all options appear right away, the
  exact capture data is read in the background afterwards.

- **Job name and file names on import:** a job like a photo trip, place or client can be entered
  in the import dialog and used as {job} in the target folder and the file name. The file name can
  be built from placeholders too, with the original name, capture date, camera and a counter that
  a RAW and its JPEG share. Like target folders, file name patterns can be defined in the settings
  and picked in the dialog.

- **Symbols found in every language:** the symbol search in the editor knows all symbols by name
  in every language of the app, and also finds them by related words, so "dog" finds the paw.

- **Shapes follow the symbol weight:** with "Filled" a new shape comes as a filled area, with thin
  to bold as an outline of matching width.

- **Bars hide when idle:** in fullscreen and in a paused slideshow the control bars and the mouse
  pointer now disappear after a moment without movement, just like while the slideshow plays.

### Fixes

- Lighting: a picture without an edit no longer shows the lights of the picture opened before it.
- Import: a target folder pattern is remembered for the next import. New placeholders {make} and
  {model} give the camera's make and model on their own, {camera} no longer repeats the make, and
  videos without camera information go to "Video" instead of "No camera".

- Import: when the import button is disabled, the summary now says why, for example a target
  outside the folders FerrumPix may write to, and where to allow it.

- Distort: the live preview of Grid and Warp now appears from the first drag, also at the
  corners, instead of only after a second handle was moved.

- Smaller close and clear crosses everywhere, and the frame switch next to the editor's zoom is
  gone; the frame is switched in its own group.
