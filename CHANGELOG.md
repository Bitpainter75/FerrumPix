## 0.9.60

### What's new

- **Any picture can become a layer.** RAW files, PSD, FerrumPix projects, HEIC, JPEG XL and SVG
  can now be added to a picture in the editor as a layer, by dragging them from the filmstrip or a
  file manager, through the insert tool or the filmstrip menu. A RAW comes in developed, with its
  edits if it has any, a PSD or project as its finished picture.

- **Perspective fills the frame on its own.** A new "Fill automatically" option keeps the size
  adjusted while you correct the perspective, so no empty corners are left. When you drag the
  corners in the picture, it fills the frame as you let go.

- **Remove the sensor border.** A new switch under lens correction cuts off the outermost pixel
  rows of a RAW that the sensor does not deliver cleanly and that can show up as colored stripes
  along the edge. It is off by default and offered for more than 300 camera models.

- **Image brush.** The eraser now shares one tool with four new brushes that change the picture
  instead of painting on it: dodge and burn lighten or darken, aimed at the shadows, midtones or
  highlights, the sponge takes color away or adds it, and replace color recolors while keeping
  brightness and texture. They work on the photo and on a selected picture layer.

- **Refine edge.** A new button in the selection tool and in the mask tool makes the edge of a
  selection or mask follow the picture within a narrow band, so hair, fur and fine twigs are picked
  up instead of being cut off hard. It needs no model.

- **Refresh in the folder tree.** Right click a folder and choose "Refresh" to read its subfolders
  again, for example after changes in a file manager. Folders you had opened stay open.

### Fixes

- Lens correction recognises Sigma and Tamron lenses on Canon, Sony and Fujifilm cameras. Before,
  it often picked a lens of the camera maker with similar numbers, and a Sigma 24-70 on a Canon
  was corrected like a Canon 24-70.

- On Nikon cameras many more Sigma, Tamron and Tokina lenses are recognised by name, among them
  the Sigma 17-50mm f/2.8, 35mm and 50mm Art and the Tamron SP 24-70mm, and older bodies such as
  the D40, D200 or D300 now report them too. A Tamron 24-70 is no longer corrected like a Nikon
  24-70.

- Lens correction no longer mixes up lenses with a different focal length or brightness, such as a
  10-20mm zoom taken for a 20mm lens. When no matching lens is known, the photo is left as it is.

- The lens search field also understands input like "Sigma 2.8/17-50", and when nothing matches it
  says so instead of staying silent.

- On Windows the catalog keeps one entry per photo, however its path is spelled. Before, the same
  file could be stored twice, read in again and carry two different ratings. Existing catalogs are
  merged once on the first start.

- The notice that a camera is not supported no longer appears for cameras that are, such as the
  Nikon Z 6II, Z 7II, Z 8, Z 9 and Z f, the OM-1, the Panasonic S5II or phones that save DNG.

- Film negative conversion measures the picture again after cropping. Before, film edges,
  sprocket holes or the scanner holder that were cropped away afterwards still threw off the
  colors. A film base picked with the eyedropper is kept.

- PSD files in 16 and 32 bit open with their layers, as do large PSB files. Before, they
  opened as a single flattened picture. A layer's fill opacity is now taken into account.

- Resetting the effects also takes lens correction back out of the picture. Before, only the
  switches went back while the picture stayed corrected.

- Film negative conversion leaves out the black scanner holder at the edges even without
  cropping. Before, it made the converted picture much too dark.

- Automatic enhancement works on film negatives. Before, it looked at the negative instead of the
  converted picture and pushed it the wrong way, often much too cold and too dark.

- Saving a picture twice in a row with "Save as" keeps retouching, brush strokes, denoising and
  removed objects in the second file too. Before, they were missing there even though the editor
  still showed them. The same applied to saving a project a second time.

- The arrow keys scroll the settings page, as Page Up and Page Down already did.

- On smaller gallery tiles the EXIF, IPTC, XMP and ICC badges no longer sit under the rating stars.
  When there is not enough room for both, only the stars are shown; larger tiles show both.
