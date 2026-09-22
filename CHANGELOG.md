## Unreleased

### What's new

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

### Fixes

- A picture with objects on it looks the same wherever you see it. An object that was turned and
  pulled into shape at the same time came out a little differently in the viewer, in an export and
  in print than it did in the editor. They all put the scene together the editor's way now.

