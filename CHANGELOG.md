## Unreleased

### Fixes

- The fast AI denoiser no longer turns very dark, smooth areas such as a night sky or a black
  stage curtain into a colored pattern.

- After choosing a folder in the folder tree with the mouse, the arrow keys, PAGE UP, PAGE DOWN,
  HOME and END move through its pictures instead of switching to another folder. The same goes
  after a right-click menu or a middle-click preview in the gallery.

- The details under gallery tiles no longer run into each other on small tiles; a detail that
  does not fit is left out instead.

- The info panel keeps labels and values tidy in a narrow panel: values stay right-aligned, long
  names break between words, and the camera name no longer repeats the brand. Dimensions show the
  real size of the picture, for raw files without the hidden sensor border.

- Masks: the lens correction of a raw file stays on when you switch from a mask to Adjust, Color,
  Details, Effects or Filter, and Feather now works on a luminance or color range mask after you
  go back to the mask tool.

- The brush size changes in larger steps with [ and ] and the mouse wheel, and the brush circle
  follows right away instead of waiting for the mouse to move.

- RAW files from the Canon EOS R6 Mark III are developed correctly instead of coming out flat
  and with a magenta cast.

- The optional setting that adapts the base brightness to the camera model now matches Lightroom
  much more closely and covers more cameras, including older raw formats. It stays off by
  default.

- On Windows, HEIC pictures are read with an updated library that closes several security
  issues, among them one where a crafted file could use up large amounts of memory.

- Measuring the denoise strength no longer mistakes fine detail in small or downsized pictures
  for noise. Pictures under three megapixels are left alone by the automatic strength; they can
  still be denoised with a strength you set yourself.

