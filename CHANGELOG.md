## Unreleased

### What's new

- **Your photos on a map.** The gallery can show the current pictures by where they were taken,
  grouped into circles with a count; clicking one lists its pictures below the map. The map is off
  until you turn it on in the new Map view section of the settings, because its tiles come from a
  server on the internet, which learns which area you look at. Tiles you have seen stay on your
  device until you clear them, and the tile server address can be changed. The zoom slider at the
  bottom zooms the map, and the keys from the viewer work there too: F shows all pictures, Z jumps
  to a close-up under the mouse pointer and back, plus, minus and the arrow keys zoom and move.

- **Compare against the camera's own JPEG.** The compare button in the editor has a small menu that
  puts the JPEG the camera stored inside the raw file on the before side. It shows where FerrumPix's
  development differs from the manufacturer's look of the same shot. It only appears when the file
  carries a usable preview, and it goes back to your own development for every new picture.

- **Z, F and SHIFT+arrow keys in the editor.** As in the viewer, Z switches between fit and 100
  percent, and it zooms in on the spot under the mouse pointer. F fits the picture, and SHIFT+arrow
  keys move the visible area as long as no object is selected.

### Fixes

- DNG files from CHDK cameras develop as they did up to 0.9.47 again, without coming out too bright.

- Presets and sidecars from Lightroom or darktable keep their full exposure range of five stops in
  both directions, for local adjustments as well; strong values were cut off before.

- Some third-party lenses on Nikon cameras that only identify themselves by a coded ID are now
  named correctly, among them the Sigma 18-35mm f/1.8 Art and the Tamron SP 70-300mm VC.

- Home and End in the photo wall jump to the first and last picture again.

