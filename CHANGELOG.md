## Unreleased

### Changes

- **Only your photo folders in the container.** In the Docker image FerrumPix now shows just
  `/photos`: the folder tree, every folder and file chooser, Save as, Export to and the batch tools
  stay inside it, and it is the home and start folder. Other folders can be set with
  `FERRUMPIX_FOLDER_ROOTS` or `--folder-roots=`; without them nothing changes.

- **Download whole folders in the browser.** Folder tiles in the gallery now have the download
  badge too, and Download is in the right-click menu of a folder tile and of the folder tree. The
  folder comes as one ZIP named after it, with its subfolders and sidecar files; hidden files are
  left out. A very large folder asks first, an empty one says so.

### Fixes

- In the container, deleting, renaming, moving and copying pictures under `/photos` work again
  without switching on file operations outside the personal folder, and the download badge on the
  tiles and Download in the right-click menu appear again. Downloading only reads a picture and no
  longer depends on permission to change it.

- The search field in the gallery no longer loses the keyboard while you type when a catalog
  search, the catalog index or a face search is running; before, you had to click into the field
  again after every letter. The field now stays next to the running search, and a catalog search
  starts once you pause typing for a moment.

- In the Flatpak from Flathub, importing into a folder in your home folder that the Flatpak can't
  see, such as `~/2026`, no longer puts pictures where they vanish after closing FerrumPix (GitHub
  #166). FerrumPix now warns in the import dialog and writes nothing there, and the same goes for
  Save as, exporting, the batch tools, pasting, moving, new folders, backups and the data folder.
  The message explains how to share the folder with Flatseal or `flatpak override`, or to pick it
  with the folder chooser.
