## 0.9.72

### Fixes

- **Canon EOS R8 Mark II colours:** its RAW files were developed as if shot in daylight, because
  the camera's white balance was not read. They now use the white balance set in the camera.

## 0.9.71

### What's new

- **Fujifilm lens corrections from the camera:** when the lens database has no profile for your
  Fujifilm lens, FerrumPix now corrects its distortion and vignetting with the values the camera
  stores in the RAF file, as it already did for distortion with Sony, Olympus and Panasonic.

- **More processor cores for the models:** a new setting under Performance decides how many
  processor cores denoising and the other models may use. By default it is still half of them, so
  the app stays responsive; more cores make the models faster when they run on the processor.

- **Copy and paste adjustments in the gallery:** right click an edited RAW or PSD and choose
  Adjustments, Copy adjustments, then paste them onto a whole selection. It uses the same clipboard as copy and
  paste in the editor, so you can copy in one place and paste in the other. RAW and PSD files
  get the adjustments in their sidecar file; JPG, PNG and WebP are overwritten after asking.

- **Detail sliders named and grouped as in the reference program:** the luminance noise reduction,
  formerly Smoothing in the Blur group, now sits as Luminance with its Detail and Method in the
  renamed Noise Reduction group, next to Color, and sharpening is Strength, Radius, Detail and
  Masking. Color there only removes color noise now. Adding noise and color noise moved to the
  Grain group as the Noise and Color noise sliders, together with Dust/scratches, which now
  only adds and goes further at its maximum. Blur has no group of its own any more and sits in
  Details. Vignette moved to the Details tool below Sharpen, Depth blur to the Effects tool below
  Threshold, and Frame now opens the Effects tool above Grain. The rarely useful Color blotches and Blotch size sliders and the negative halves of
  Noise and Dust/scratches are gone; pictures edited with them keep their look.

- **Whites and Blacks keep the colours:** lowering Whites or raising Blacks no longer drains colour
  from bright areas and midtones; the colours now stay as strong as in the reference program.
  Pictures that use these sliders look a little more colourful than before.

- **Histogram next to the sliders by default:** the analysis graph now sits above the adjustment
  sliders from the start, as Histogram in Adjust, RGB parade in Color and Waveform in Details and
  Effects. Switching it in a tool is remembered as before, and Settings can turn it off. It is
  also switched on once after the update, so if you had turned it off before, turn it off again.

- **Display profiles on Windows and Linux:** in Settings you can choose an ICC profile for each
  connected display, for example from a calibration. Photos in the viewer and editor are then
  shown correctly on the display the window is on, also on wide gamut monitors. Leave it empty
  where the system already does this, such as KDE Plasma with a color profile set.

- **Shorter context menu:** applying, copying and pasting adjustments and the watermark now sit
  together in an Adjustments submenu, rejecting and the series entries in a Sort out submenu. When
  only one of them is available, it stays in the main menu.

- **One graphics card for everything on Linux:** on laptops with two graphics cards, the card you
  choose in Settings under Performance is now used for image editing as well, not only for the
  models. It takes effect after a restart, and the window is then drawn on that card too. The
  same choice is also offered when a model is downloaded, next to the option to use the card.

- **Naming people photo by photo:** the People tab in the sidebar now stays open while you flip
  through your photos, also on photos without faces. Names the recognition filled in on its own
  are shown in italics with a check mark next to them; click it or press ENTER to confirm the
  name, so you can see at a glance which photos you have already been through. TAB moves from
  name to name, and after the last one straight to the first name in the next photo.

- **Stacks on and off from the gallery:** a button in the gallery footer, next to the selection
  buttons, turns the stacking of bursts and RAW+JPEG pairs on and off. It is the same setting as
  in Settings, so both always show the same state.

- **Sharpening and colour noise reduction like the reference:** sharpening now works on brightness
  only, so it no longer strengthens colour fringes and colour noise, and strong edges get smaller
  halos. Colour noise reduction measures the noise in each picture and removes only that, instead
  of washing out real colour. Both were matched against renders of the reference program, so
  presets with sharpening and noise reduction come out much closer. Pictures that already use
  sharpening or colour noise reduction will look somewhat different.

- **No more jagged edges and speckles from the colour mixer:** colour noise reduction now runs
  before all colour and tone adjustments, and the colour mixer no longer changes the brightness of
  nearly grey, noisy shadows. Presets that treat neighbouring colours very differently left jagged
  edges around coloured lights and speckles on calm surfaces.

- **Calmer colours with presets that use the colour mixer:** neighbouring colour bands now blend
  smoothly into each other, and the colour mixer decides on a slightly smoothed colour which band
  a pixel belongs to. Presets that treat two neighbouring colours very differently left blotchy,
  speckled areas where a colour sits between them, for example on lettering in the background.

- **Sharper colour edges in saved JPEGs:** JPEG files you save or export, and collages, now keep
  the full colour resolution instead of halving it. Coloured edges such as stage lights and text
  stay crisp, the files get about a third larger.

- **More colour in blown highlights:** where a RAW file clipped only some colour channels, for
  example in a bright sky or a lit face, FerrumPix now rebuilds the missing channel from the others
  and from the colours around it. Pulling down exposure or highlights brings back more natural
  colour there instead of flat white, while bright lamps and the sun stay neutral.

- **Camera calibration closer to the reference:** shadow tint now works across the whole picture
  like in the reference program instead of only in the shadows, where it was far too strong and
  turned red and skin tones. All calibration sliders now have the strength of the reference,
  several of them used together no longer cancel each other out, and together with the curves of
  a preset they no longer push reds too far. The colours matched to each camera model (an option
  in Settings) have been measured again on this basis, for 80 models.

- **True colours under artificial light:** most cameras come with two colour profiles, one for
  daylight and one for warm tungsten light. FerrumPix now blends between them according to the
  white balance of each RAW file, as the reference program does, instead of always using the
  daylight one. Photos under tungsten, stage or other warm light come out with clearly more
  accurate colours, daylight photos change only slightly. The colour temperature shown for a RAW
  file is calculated the same way and can differ a little from before.

- **Preset amount:** after applying an XMP preset, an Amount slider appears in the preset panel.
  0 % is the picture without the preset, 100 % the preset and 200 % its changes twice. It goes
  away with the next edit, as in the reference program.

- **Presets with colour curves apply completely:** presets that combine red, green and blue curves
  with a tone curve, about a third of common presets, used to leave out their tone sliders, colour
  mixer, calibration, sharpening and noise reduction. They now apply in full and come out clearly
  closer to the reference program. Colour curves without a tone curve are ignored there, and now
  here as well.

- **Starting values for sharpening and noise:** a RAW without edits now starts with sharpening 40
  and colour noise reduction 25, like in the reference program. In Settings you can change these
  starting values and set one for noise reduction as well. Resetting all edits returns to them.
  A colour noise starting value of 0 is raised to 25 once after the update; set it back to 0 if
  you chose that on purpose.

- **Whole pictures in the gallery grid:** a new setting shows each picture complete in its tile,
  portrait or landscape, instead of cropping it to the tile.

- **Sensor border removed by default:** RAW files now start without the thin strip of unusable
  pixels at the edge of the sensor, in the gallery, the viewer and the editor. You can switch it
  off for a single picture under lens correction. Pictures you have already edited stay as they
  were.

- **Search a single photo for people:** when a photo has never been searched for faces, the People
  tab in the sidebar now says so and offers a button that searches just this photo. The message
  that nobody was recognised only appears once the photo has actually been searched.

- **People in RAW and HEIC photos:** face recognition now also finds people in RAW files, using
  the picture the camera embedded in them, and in HEIC, JPEG XL, TIFF and PSD files. Before, these
  photos were skipped without a word. Faces in RAW files also show their thumbnails in the sidebar
  and are written to the sidecar file like those in JPEGs. Photos in these formats are searched
  again the next time you search for faces. A RAW with the JPEG the camera saved next to it counts
  as one photo for a person.

- **Undo a reject from the tile:** clicking the red reject badge on a gallery tile takes back the
  reject for that photo.

- **Settings open faster:** the settings page is now prepared bit by bit while you are not using
  the app, so opening it for the first time is as quick as every time after.

### Fixes

- **Old Canon RAW files no longer come out yellow:** CRW files from cameras such as the
  PowerShot G3, G5, G6, S50, S70, Pro1 and EOS D30 that were shot with automatic white balance
  were developed as if in daylight, so pictures taken under indoor light looked deep yellow.
  FerrumPix now works out the white balance from the picture for them.

- **Raising the exposure of a RAW no longer flashes strong colours too bright:** until the
  picture was rebuilt in the background, strongly coloured areas looked brighter than afterwards.

- **Gallery tiles update after a batch:** after pasting or applying adjustments to RAW and PSD
  files, the tiles kept showing the old picture without the edited badge until they were reloaded.
  They now update right away.

- **Saving your own gradient presets:** a gradient saved as a preset in the fill settings did not
  appear in the list. It is now saved and can be removed again with the bin next to the list.

- **Display profiles on KDE Plasma:** the settings could say that no displays were found until
  the window was moved once. The displays are now listed as soon as the settings open.

- **Less memory over long sessions:** FerrumPix no longer keeps the full metadata of every
  picture it has ever read. With large watched folders this used to add up to a lot of memory.

- **When deleting fails, the picture stays:** if a picture cannot be deleted, for example because
  the trash is not available, the viewer, the editor and the search results keep showing it
  instead of acting as if it were gone.

- **Graphics card on macOS:** the graphics card option now works on Macs as well. Before, it
  always fell back to the processor there, with an untranslated reason in the settings. When the
  card cannot be used, the settings now give the reason in your language.

- **Phone RAWs no longer have dark corners:** DNG files from phones such as Google Pixel, Xiaomi
  and Nokia or Microsoft Lumia carry a correction for the lens's shading. FerrumPix now applies it,
  so their corners are no longer up to two stops too dark.

- **Files that differ only in upper and lower case:** on Linux, IMG.JPG and IMG.jpg
  in the same folder are now always treated as two pictures. Before, deleting both removed only
  one, the gallery could fail to show new ones, and the viewer and editor could jump to or drop
  the wrong one.

- **Faster browsing of RAW files in the viewer:** when the viewer develops RAW files, moving on
  to the next picture now stops the development of the previous one instead of waiting for it to
  finish.

- **Closed eye in the adjustment groups easier to see:** while a group is hidden for comparison,
  its closed eye now uses the text colour, which stands out on every theme, including the grey
  ones.

- **Film negatives without a colour cast from grain:** on grainy colour negatives, the grain of
  the scan could decide the inversion and tint the whole picture, more so on a large monitor.
  FerrumPix now measures the film so that the grain no longer counts, and the same scan turns out
  the same on every screen.

- **Readable with the yellow accent:** text and icons on accent coloured areas were white on
  yellow, for example on the selected layer, the badges on gallery tiles, the drop hints and the
  tick in checkboxes. They now turn dark whenever white would not stand out.

- **Soft Light blending corrected:** over dark areas, layers and PSD files in Soft Light came out
  too light. Pictures that use Soft Light can look a little darker there than before.

- **More LUT files work:** .cube files with a 1D curve in front of the 3D table, pure 1D LUTs and
  LUTs with their own input range used to have no effect at all. They are now applied as the file
  describes.

- **XMP presets with the profile written in another form:** the profile and its amount were lost
  when a preset stored them as separate entries instead of attributes. Both forms are now read.

- **Automatic perspective correction only where lines really converge:** a single slanted edge,
  branches or other edges in the picture could make it tilt photos without any architecture, often
  as far as it goes. It now only corrects when most edges run towards a common vanishing point,
  and leaves the rest alone.

- **Tooltips in the adjustment groups translated:** the tooltips of the compare and reset buttons
  in the group headers sometimes appeared in German, for example in Colour, Colour mixer and
  Colour grading.

