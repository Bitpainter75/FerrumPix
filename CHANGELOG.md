## Unreleased

### Fixes

- The fast AI denoiser no longer turns very dark, smooth areas such as a night sky or a black
  stage curtain into a colored pattern.

- After choosing a folder in the folder tree with the mouse, the arrow keys, PAGE UP, PAGE DOWN,
  HOME and END move through its pictures instead of switching to another folder. The same goes
  after a right-click menu or a middle-click preview in the gallery.

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

