## 0.9.63

### What's new

- **Bursts and RAW+JPEG pairs as stacks.** When switched on in Settings, shots taken in quick
  succession and the camera JPEG next to a RAW appear as one thumbnail. A click on a burst shows
  just that burst until you go back to the folder; pictures of a burst are deleted there one at a
  time, never the whole burst at once. The navigation buttons only leave the burst view and stay
  in the folder. Ratings, labels, keywords and deleting on a pair apply to both files.

- **Sharpest shot first.** Within a burst the sharpest shot moves to the front, and each shot shows
  how sharp it is compared to the best one.

- **Reject while culling.** X marks pictures as rejected, SHIFT+X rejects everything in a burst
  except the chosen shot. Rejected pictures are dimmed and can be hidden or shown on their own with
  the filter; the mark is written to the sidecar as well.

- **File operations outside the home folder.** A new setting allows copying, moving, renaming and
  deleting on other drives, mounted media and network folders. It is off by default, and system
  folders stay locked either way.

- **Eight editable looks in Filters.** Base Light, Color Harmony and Luminous Curve highlight
  individual adjustment groups; Desert Light, Film Grain, Color Haze, Clear View and Timeless
  offer further starting points. Every look consists only of ordinary FerrumPix sliders,
  HSL settings, curves, grain and vignette, so it remains fully editable after applying it and is
  also available in batch adjustments and Export to.

- **A clearer editor footer.** The output dimensions no longer compete with the centered zoom
  controls on smaller windows.

- **Accent-colored window logo.** The “Pix” part of the title-bar logo now follows the selected
  accent color and strength.

### Fixes

- **Stars, heart and metadata badges on thumbnails now sit on one line**, the heart has the same
  color as the stars, and the smallest thumbnail size leaves room for every badge.

- **Missing translations filled in:** the rename and convert dialogs, the window button setting,
  the watermark offsets and the shape names in the editor.

- **Color grading now tones black-and-white pictures.** Before, the black-and-white look turned
  every toning back into gray.

- **The viewer comparison no longer puts a picture next to itself.** Paging skips the pinned
  picture, and it is dimmed in the filmstrip.

- **Lens data below thumbnails now comes from the same actual focal length as the EXIF panel.**
  A 50 mm lens is no longer labelled 75 mm on a crop camera, and existing catalog entries are
  refreshed automatically. This also refreshes missing lens names, including Sigma lenses, and
  lenses added to FerrumPix's lens list later now reach photos that were already catalogued.

- **HSL color chips now refresh their change indicators** when an image is loaded or switched, or
  when global adjustments are reset.

- **More precise size controls.** Frame width and margin, shadow size and glow size can be adjusted
  in 0.1 steps.

- **Mouse-wheel input on numeric fields no longer builds up a delayed queue.** Rapid extra wheel
  impulses are discarded instead of continuing to change the value after scrolling stops.

- The frame group only appears for the main image or the selected frame layer. A small frame
  toggle in the image footer shows whether the frame is active. Frame layers cannot be duplicated
  or copied and pasted, keeping at most one frame layer in a project.

- **More precise numeric controls** are available for tone and color adjustments, color grading,
  detail effects, white balance and layer opacity. Kelvin white balance uses 10 K steps.

- Removing a layer's adjustments while editing that object no longer lets stale adjustment values
  reappear when changing its effects.

- Pasting a copied mask or selection without a selected layer creates a new mask or selection layer.

- Double-clicking an overlay object now opens its matching editing tool; a single click still leaves
  the current adjustment tool active.

- Image text now uses HarfBuzz shaping for Thai and other complex scripts, with shaped widths also
  used for drawing and multiline alignment.
