## 0.9.54

### What's new

- **Colors matched to the camera model.** A new optional setting starts unedited raw files with
  calibration values that bring their colors close to a widely used raw development, tuned
  separately for many camera models. The values show up in the Calibration sliders and can be
  changed there; pictures you have already edited stay as they are. It is off by default.

- **Choose the details under gallery tiles.** Settings, Gallery lets you pick what appears below
  the file name: up to three lines, each with one detail on the left and one on the right, such as
  dates, size, file type, camera, lens, exposure or place. The list view shows the same details.
  By default the tile now shows the date the picture was taken instead of the date the file was
  last changed.

### Fixes

- The fast AI denoiser no longer turns very dark, smooth areas such as a night sky or a black
  stage curtain into a colored pattern.

- After choosing a folder in the folder tree with the mouse, the arrow keys, PAGE UP, PAGE DOWN,
  HOME and END move through its pictures instead of switching to another folder. The same goes
  after a right-click menu or a middle-click preview in the gallery.

- The details under gallery tiles no longer run into each other on small tiles; a detail that
  does not fit is left out instead.

- The rating stars on gallery tiles sit closer together, and the heart now lights up under the
  mouse like the stars do.

- The info panel keeps labels and values tidy in a narrow panel: values stay right-aligned, long
  names break between words, and the camera name no longer repeats the brand. Dimensions show the
  real size of the picture, for raw files without the hidden sensor border.

- Masks: the lens correction of a raw file stays on when you switch from a mask to Adjust, Color,
  Details, Effects or Filter, and Feather now works on a luminance or color range mask after you
  go back to the mask tool.

- The brush size changes in larger steps with [ and ] and the mouse wheel, and the brush circle
  follows right away instead of waiting for the mouse to move.

- RAW files from the Canon EOS R6 Mark III are developed correctly instead of coming out flat
  and with a magenta cast, at every ISO setting.

- RAW files from most Canon cameras since about 2010 use the black and white levels the camera
  writes into each file. Blown highlights now turn white instead of gray, and pictures are no
  longer developed too dark.

- The optional setting that adapts the base brightness to the camera model now matches Lightroom
  much more closely and covers more cameras, including older raw formats. It stays off by
  default.

- On Windows, HEIC pictures are read with an updated library that closes several security
  issues, among them one where a crafted file could use up large amounts of memory.

- Measuring the denoise strength no longer mistakes fine detail in small or downsized pictures
  for noise. Pictures under three megapixels are left alone by the automatic strength; they can
  still be denoised with a strength you set yourself.

