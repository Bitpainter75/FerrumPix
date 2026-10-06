## 0.9.63

### What's new

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

- **Lens data below thumbnails now comes from the same actual focal length as the EXIF panel.**
  A 50 mm lens is no longer labelled 75 mm on a crop camera, and existing catalog entries are
  refreshed automatically. This also refreshes missing lens names, including Sigma lenses.

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
