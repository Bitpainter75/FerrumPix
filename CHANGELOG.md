## FerrumPix 0.9.34

### What's new

- **You choose what the info sidebar shows.** The General tab used to be a fixed list, and it left out what matters most while sorting through pictures. Camera, lens, aperture, shutter speed, ISO, focal length and the date the picture was taken are now on it, and every line has a tick of its own in Settings - the folder, the place and the copyright line too. Megapixels, aspect ratio and colour space are off to begin with; tick them and they come back. A line stays away when the picture has nothing to put on it.

- **Rating, label and keywords can be taken out of the sidebar as well.** Three ticks for the three sections below the tabs, for anyone who keeps those elsewhere.

- **Everything about the info sidebar is in one place in Settings.** It has its own section now. The switches for it used to sit under Gallery, Viewer and Editor, three places for one sidebar. The sidebar also goes by one name throughout the interface now, where there used to be three.

- **The gallery says what it is still doing.** While preview images are being made and metadata is
  being read, the footer names both, with a count. Until now the only sign was that things kept
  appearing.

- **A drawing tablet setting.** With a pen you could drag the sliders but not press anything.
  Buttons and menu entries normally act when you let go, and with a pen that second half does not
  always arrive. Switch this on under Settings, General, and everything acts the moment the pen
  touches down. The price, and it applies to the mouse as well: a click can no longer be taken back
  by moving away before letting go. With a mouse, leave it off.

  
### Fixes

- **Scrolling is smoother.** Every tile and every row that came into view made the gallery lay out
  all of them again. Both keep what they have now and reuse it. Grouped by day, month or year the
  gallery works as before - there the tiles share their row with the headings, and that needs the
  old way of laying out.

- **Opening a folder is quicker.** Reading a folder also fetched everything the catalogue knew about
  every folder underneath it, even when the folder itself held no pictures at all. It now asks only
  about the pictures it is about to show, and only once instead of twice.

- **A folder you have opened before shows up straight away.** What the catalogue already knows is on
  screen, folders and all, while the folder itself is still being read. Nothing moves or is redrawn
  when the reading finishes. Pictures that were deleted elsewhere in the meantime are still shown
  for that moment and then disappear.

- **Leaving a large folder no longer holds up the next one.** Reading a folder carried on in the
  background even after you had moved on, and the folder you had just opened waited for it.

- **The timeline and the sort by creation date work in search results.** Pictures found by a search
  carried no file dates until you looked at them, so the timeline said "no date" and sorting by
  creation date sorted by nothing. The dates come from the catalogue now.

- **The list of sections in Settings scrolls** when the window is too short for it. The last entries
  used to be simply out of reach.

- **The preview cache holds 500 pictures instead of 250**, and you can set it up to 10000. Scrolling
  back over pictures you have already seen loads nothing again, as long as they fit.

- **Pictures from a server are shown under their own name.** In the information sidebar you saw the name of the working copy, which is the asset's identifier, instead of the name of your photo. The folder line is gone there too: it named a temporary folder that has nothing to do with the picture.

- **Lens correction works on raw files again.** The name of the lens was there in the sidebar, but the correction found nothing to apply. A raw file carries several sets of shooting data, one of them belonging to the small preview picture inside it, and that was the one being read. Reported and traced by atleag.

- **The sliders in the editor follow your hand.** The picture used to wait for the drag to end
  before it caught up. It now redraws while you drag, on a smaller version of the picture where
  that does not change what you see, and at full size the moment you let go. A change made while a
  preview was still being drawn is no longer dropped.

- **Editing a picture is quicker.** Grain was by far the most expensive step and held up every
  other slider with it, because it had to be worked out one pixel after another. It is now
  calculated from the position in the picture, which lets the whole picture be done at once.
  Please note: the grain pattern is a different one now. It is as strong and as fine as it was, but
  a saved recipe with grain in it will not look pixel for pixel the way it did.

- **The warp grid sits on the part of the picture you can see.** After a crop it was still laid out
  over the whole original, so the handles were in the wrong places and the tool was hard to use.

- **Depth blur can be stopped while it is working**, and stopping it leaves the picture untouched.
  Until now the stop only took effect once the blur had finished.

