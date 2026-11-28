## Unreleased

### Changes

- **Familiar share icon on the phone page.** Sharing now uses the usual icon with three connected
  dots.

- **Square icon on the home screen.** The phone page added to the home screen now shows a proper
  square icon instead of one with odd corners.

- **Exposure times in exported files.** Times such as 0.4 or 0.8 seconds that are filled in from
  the RAW file no longer turn into 1/2 or 1 second in the exported picture.

- **Camera JPEGs stay safe when exporting next to the original.** If only the RAW file is
  selected and its camera JPEG lies next to it, the export now gets a free name instead of
  replacing the camera picture, even after "Overwrite all"; on Linux it no longer ends up as a
  second file that only differs in upper and lower case.

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

