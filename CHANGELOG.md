## Unreleased

### What's new

- **Any picture can become a layer.** RAW files, PSD, FerrumPix projects, HEIC, JPEG XL and SVG
  can now be added to a picture in the editor as a layer, by dragging them from the filmstrip or a
  file manager, through the insert tool or the filmstrip menu. A RAW comes in developed, with its
  edits if it has any, a PSD or project as its finished picture.

### Fixes

- Lens correction recognises Sigma and Tamron lenses on Canon, Sony and Fujifilm cameras. Before,
  it often picked a lens of the camera maker with similar numbers, and a Sigma 24-70 on a Canon
  was corrected like a Canon 24-70.

- Lens correction no longer mixes up lenses with a different focal length or brightness, such as a
  10-20mm zoom taken for a 20mm lens. When no matching lens is known, the photo is left as it is.

- The lens search field also understands input like "Sigma 2.8/17-50", and when nothing matches it
  says so instead of staying silent.

- On smaller gallery tiles the EXIF, IPTC, XMP and ICC badges no longer sit under the rating stars.
  When there is not enough room for both, only the stars are shown; larger tiles show both.
