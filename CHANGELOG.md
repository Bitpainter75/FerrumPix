## Unreleased

### Changes

- More comes along from a Lightroom Classic catalog: capture times you corrected there (a fixed
  camera clock, the date you gave a film scan), copyright, GPS position, city and country. They
  only fill what the picture itself does not say, and anything you set later in FerrumPix wins.
  For raw files the copyright and location are kept next to the picture, so they stay even
  without the catalog. DNG and TIFF files also use a capture time saved into the file itself.

## 0.9.79

### Changes

- A new, crisp app icon, redrawn by Mibea. Many thanks for the careful work! On macOS the icon now
  also comes at full Retina size.

- In the viewer and in fullscreen, HOME jumps to the first picture of the folder and END to the
  last.

- Dodge and burn work more gently and stay in their tone range: shadows, midtones and highlights
  each have their own curve, and the new "Protect tones" option (on by default) keeps colors,
  black and white as they are.

- Brushes, masks, selection brushes and retouching in the editor now go up to a size of 3000.

- A Lightroom Classic catalog can now be read alongside the RawTherapee and ART files (Settings,
  Catalog). Ratings, pick flags, color labels and keywords come into the catalog, and raw files
  you edited in Lightroom open with that edit and keep their title and caption. The catalog itself
  is only read, and it also works with a catalog from another computer as long as the folders kept
  their names.

- Titles and descriptions of raw files are now also kept in FerrumPix's own sidecar file, next to
  the XMP file other programs read.

- DNG files compressed with JPEG XL, the small lossy DNGs that current raw converters and some
  phones save, now open with their full raw data in the editor instead of only the embedded
  preview.

- **Fog by depth** now behaves like real haze: it builds up with the distance from the camera, so
  even the nearest subject gets a little, middle distances get a fair share and the far background
  the most. Start and full density spread evenly over that distance, and by default the fog starts
  at the camera. Fog you applied before stays as it is.

### Fixes

- Fog by depth, depth blur and the mask by depth now follow the outlines of people and objects
  closely. Before, a clear band without fog or blur was left around a subject in front of the sky
  or foliage.

- Brush, eraser, retouching and the mask and selection brushes paint again where their circle lies
  over the picture when part of the circle reaches past the edge, also on a rotated or cropped
  photo.

- Raw files from Samsung phones in Expert RAW or Pro mode no longer open several stops too dark:
  the brightness the phone notes in the file is now used. Some of them still come out a little
  darker than on the phone.

- A Photoshop file whose fill or shape layers carry no pixels, as Photoshop saves them in 16 and
  32 bit, no longer opens as a blank white picture: FerrumPix says so and offers the flat picture
  instead, and shows the flat picture when you browse to it.

- If the saved edit of a raw or Photoshop file cannot be read, FerrumPix now says so when you
  open the picture instead of quietly opening it unedited. The file with the edit is left as it
  is.

- Nextcloud: saving an edit no longer quietly replaces one that another computer saved in the
  meantime; FerrumPix asks first. If the edit on the server could not be read when the picture was
  opened, it is not overwritten. Writing ratings, labels and keywords into XMP files on the server
  no longer replaces an existing XMP file after a failed download, and keeps its name.

- A file name with a dot in it, such as an aperture like "f1.4", is no longer cut short when saving,
  renaming, renaming several files at once or naming a collage, and the file keeps its extension.

- Memory no longer keeps growing during a long session. Switching to the viewer or deleting photos
  there left thumbnails behind that were never released; with high resolution thumbnails this could
  add up to many gigabytes. Deleting in the viewer also no longer reloads every thumbnail of the
  folder. With high resolution thumbnails the thumbnail memory cache now goes up to 5000 instead of
  10,000, since each of them needs about four times the memory.

