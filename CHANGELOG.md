## Unreleased

### Changes

- **Guided perspective.** In the perspective of the distort tool you can now draw two to four
  lines along edges that should stand upright or level, and the picture is straightened so that
  they do, even when the vanishing point lies off to the side or upright and level are needed at
  the same time. A single line simply levels the picture like the line in the rotate panel.

- **Paste the geometry onto a whole series.** "Paste with selection" can now also take straighten
  and rotation, perspective and crop from the copied picture, each with its own check box, off by
  default and remembered. Handy for a series from a tripod where every shot needs the same
  correction; a picture in portrait where the copied one was landscape keeps its own crop.

- **Smoother light and color sliders.** While you drag a light or color slider in the editor,
  the picture is now drawn directly by the graphics card of the window, so it follows the slider
  more closely. After you let go, the picture is finished as before. Without a suitable graphics
  card nothing changes, and the setting under Performance switches it off. Linux only for now.

- **Browsing developed RAWs without waiting.** When the viewer develops RAW files itself, it now
  develops the next three pictures in your browsing direction and the previous one in the
  background, so they appear right away when you move on. It steps aside for anything else the
  application needs, never develops two pictures at once and can be switched off in the RAW
  settings.

- **Familiar share icon on the phone page.** Sharing now uses the usual icon with three connected
  dots.

- **Clean edges when swiping on the phone page.** Swiping through pictures no longer shows a thin
  strip of the previous or next picture at the edge.

- **Square icon on the home screen.** The phone page added to the home screen now shows a proper
  square icon instead of one with odd corners.

- **Exposure times in exported files.** Times such as 0.4 or 0.8 seconds that are filled in from
  the RAW file no longer turn into 1/2 or 1 second in the exported picture.

- **Camera JPEGs stay safe when exporting next to the original.** If only the RAW file is
  selected and its camera JPEG lies next to it, the export now gets a free name instead of
  replacing the camera picture, even after "Overwrite all"; on Linux it no longer ends up as a
  second file that only differs in upper and lower case.

- **DHT as a further RAW method.** The method for developing the sensor data now also offers
  DHT, with less false colour on fine red and green patterns at about the same speed. The
  default stays as it is, so no picture changes.

- **Own setting for Fujifilm X-Trans.** X-Trans files now have their own choice of one or three
  passes; one pass looks the same and takes about half the time. The method above now only
  applies to all other cameras, and earlier choices carry over unchanged.

- **Presets with a transform no longer zoom in.** Taking over the transform of an imported preset
  no longer enlarges the picture when the preset keeps the size at its neutral value.

- **Pictures with the wrong file extension.** A JPEG, PNG or TIFF that carries a RAW extension now
  opens as the whole picture instead of a tiny preview, an empty tile or an error message.

- **Lock screen password only for the desktop.** The password of the lock screen now only locks
  the window on your computer, and the administrator has a password of their own for the browser.
  Turning the lock screen off no longer touches the browser sign-in, and setting up the
  browser from now on no longer locks the desktop window. An existing password carries over once
  as the administrator password and stays on the desktop as well, so nothing changes after the
  update; if you only set up the browser and don't want the desktop window locked, turn the lock
  off in the settings, the browser stays protected. Turning "Protect
  with a password" on or off now also takes effect with Save at the bottom of the settings; without
  the current password the settings stay open and show what is missing. The Lock buttons show up
  only while the lock is on, without a restart.

- **System folders of a NAS stay hidden.** The folders a NAS or an operating system keeps for
  itself, such as the hidden preview folders in every folder, snapshots, recycle bins and
  "lost+found", no longer show up in the folder tree, as folder tiles, in the folder chooser, on
  the phone page or in a folder ZIP, and the catalog, search, import and shares skip them.
  Entries the catalog took from there earlier, faces included, go away with the next catalog
  scan of those folders; the files on the disk are left alone.

- **Catalog scan says why it does not start.** When the catalog scan cannot run, for example
  because another computer is using the catalog, the data folder or the watched folders are not
  reachable, or a scan is already running, it now says so instead of showing "Looking for
  images..." forever.

- **No full rescan after a time zone change.** The catalog no longer reads every picture again
  when FerrumPix starts in another time zone, for example a container started once with and once
  without a time zone, or on a share that reports file times less precisely. The catalog text of
  a picture always stays in the language of the app, also when a browser user with another
  language adds it.

- **Catalog kept after updating the container.** A container that was recreated, for example
  after an update, no longer takes its own catalog for one of another computer: it opens it right
  away, without asking to take over the lock and without reading every picture again.

- **No more empty tiles in large folders.** In folders with more pictures than the thumbnail
  memory holds, for example a large folder shown with its subfolders, tiles that came into view
  could stay empty until the tile size changed, and after a while not even that helped. Every
  tile on screen now gets its thumbnail, in the grid, the grouped view and the photo wall.

- **Videos play with the libmpv of more distributions.** On systems whose libmpv comes without
  the older OpenGL path, the AppImage said that mpv was missing and played no video at all. It
  now uses the path that libmpv has.

