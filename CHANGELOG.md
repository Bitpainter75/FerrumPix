## Unreleased

### Fixes

- **Windows: FerrumPix is in the Start menu after installing.** If the installation asked for an administrator password, the entry was put into that administrator's Start menu and was missing from yours. The setup now creates it for everyone on the computer, and it tidies up a stray entry left behind by an earlier installation.

## FerrumPix 0.9.32

### What's new

- **Put the time right when the camera clock was wrong.** *Set capture date* under Metadata gives a picture a date and time, or shifts the time it already has by days, hours, minutes and seconds - forwards or back, over a whole selection at once. A raw file is never touched: its time goes in the sidecar next to it, and FerrumPix reads it back from there. The file's own date follows the capture time, so both say the same thing.

- **See where the picture has nothing left.** A new switch under Light marks blown highlights in red and blocked shadows in blue, on the picture itself and at full zoom. It only shows where all three colour channels are against the stop, so a bright red flower does not set it off.

- **Put the image analysis where it helps.** Histogram, waveform and RGB parade can now appear in the information sidebar, above the editor's adjustment controls, or in both places. The two locations can show different views at the same time, so you can keep the tonal distribution in sight while checking individual colour channels beside the controls. Click the compact chart to open its full-size view.

### Fixes

- **Objects follow the frame again in the adjustment tools.** With Adjust, Colour, Details, Effects or Filters selected, moving, turning or resizing an object shifted only its selection frame while the object stayed where it was. It moves with the frame again.

- **No more quitting when a folder is opened.** On systems whose font setup does not name a usable standard font, FerrumPix closed itself without a message the moment a folder with pictures was opened. It now uses a font that is actually installed, and if the system has none at all, only the labels on the timeline and the rulers stay empty.

- **Pictures with their own colour profile open a little quicker.** Bringing them to sRGB no longer makes a second copy of the whole picture on the way, which also keeps memory use down with large files.

- **Changing an analysis view is quicker.** FerrumPix reuses the already calculated histogram, waveform or RGB parade while the picture has not changed, instead of decoding the image again for every switch.

- **Raw files need less memory while they open.** Developing a raw picture no longer builds the whole image a second time before handing it over, so opening a big raw leaves more room for everything else.

- **Removing an object gets going quicker.** Working out which part of the picture has to be filled no longer walks the whole photo point by point, so there is less waiting before the filling itself starts.

- **Flatpak: pictures on a network share show up.** Folders that your file manager mounts from a Samba, NFS or SFTP share used to look empty in the Flatpak version. They can now be opened like any other folder.

