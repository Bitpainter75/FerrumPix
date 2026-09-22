## FerrumPix 0.9.49

### What's new

- **A way back for raw highlights.** FerrumPix reads raw highlights without clipping so recovery
  can find the detail that is there. If that makes a particular camera look too bright, Raw
  development now has a switch to use the older development path again. It applies consistently in
  the editor, viewer and fullscreen view.

- **RAW exposure speaks in stops now.** The number next to Exposure says exactly what photographers
  expect: -1.00 means one stop darker, and the full range is -5 to +5. Existing recipes and presets
  keep their values unchanged.

- **Save as no longer takes the picture away from you.** The editor stays on the picture you were
  working on, and the saved file simply lands on disk. A setting under Editor turns it around for
  anyone who would rather carry on in the new file.

- **Picking objects adds up again.** With the plus mode, clicking another object now adds it to the
  selection instead of shrinking what you had; minus takes an object away, and so does ALT. In the
  mask tool, deselecting a layer no longer throws you into the selection tool.

- **A word when LibRaw does not know the camera.** Very new models are missing from the camera list
  of the raw library, and their files then come out flat or magenta with nothing to say why.
  FerrumPix now says it once per camera and names the way around it: convert the file to DNG, which
  carries the missing values itself. The status line of the editor mentions it as well.

- **Brushes can make a deliberate line.** Hold CTRL while painting and FerrumPix follows the angle
  of your first small movement; ALT holds a line horizontal and SHIFT vertical. This works for the
  paint brush, eraser, mask and selection brushes, healing and smudging. The clone stamp keeps its
  ALT source pick and supports CTRL only. A click with the paint brush now leaves a dot as well.

- **Downloaded AI models can leave again.** Each model group in Settings can remove the versions
  you downloaded there. Bundled and system-wide model files are never touched.

- **A gallery wall that lets pictures keep their shape.** The new fourth gallery view lays photos
  out edge to edge in their own proportions, without captions. Its gap is adjustable, and you can
  turn rounded corners and frames off separately for the gallery and the editor filmstrip.
  
- **A calmer editor on a new install.** It opens in Adjust with a roomier 380-pixel panel; existing
  choices stay exactly as they were. The exposure number and wheel now allow finer steps, and the
  gallery's thumbnail-size setting reaches larger pictures.

  
### Fixes

- DNG files that carry their own baseline exposure now start at that value, visibly and reversibly
  in Exposure. This fixes DNGs such as files made by CHDK which could otherwise start too bright;
  an existing FerrumPix recipe still takes precedence.

- Lens correction can now recognise Nikon NEFs which store the lens only in Nikon's MakerNote,
  without accidentally choosing an unrelated adapted lens. It also understands aperture values
  written with a locale's decimal comma. The standard EXIF lens name remains the preferred source
  whenever it is present.

- A picture with objects on it looks the same wherever you see it. An object that was turned and
  pulled into shape at the same time came out a little differently in the viewer, in an export and
  in print than it did in the editor. They all put the scene together the editor's way now.
