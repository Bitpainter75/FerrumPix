## FerrumPix 0.9.27

### What's new

- **The gallery can show your photos in groups.** A third view next to grid and list puts a heading with a date and a count above each block of pictures. The groups follow whatever you sort by: a date gives you one block per day, and months or years if you prefer, and sorting by name, camera, file type or rating groups by that instead. The circle in front of a heading selects the whole block at once, and picking the pictures by hand fills it in just the same.

- **FerrumPix can watch your folders.** Name them once in the settings and they are read into the catalogue in the background, subfolders included, so search and filters cover the whole collection instead of only the folders you happened to open. Later runs only look at what has changed, the run can be stopped at any time, and nothing is created next to your photos.

- **The people search can be sent over those folders in one go.** It skips the pictures it has already been through, and what it found stays saved when you stop it.

- **The Metadata menu is in the viewer and the editor too.** Setting, copying, pasting and removing a location works there just as it does in the gallery, on the picture in front of you.

- **Sharpening shows you what it protects.** Put the pointer on the Masking slider and tap ALT: the picture turns into a grey map of where the sharpening lands, bright is sharpened, dark stays calm. It stays while you drag the slider, and another tap of ALT takes it away.

### Fixes

- **The trash stays out of the way.** It no longer shows up as a folder in the gallery and the filmstrip, and nothing inside it goes into the catalogue.

- **Clearing catalogue data really clears it.** A face search running alongside could write its rows back right after the delete, leaving the catalogue half full.

- **Tidying up waits for the other window.** Cleaning catalogue data or thumbnails while a second window was indexing could bring entries back or mix up the thumbnail index; it now says so and asks you to try again afterwards.

- **A folder link that points back at itself no longer stalls the indexing run.** Linked folders are still read, each one once.

- **Network drives are there right after installing on Windows.** The setup ran FerrumPix with administrator rights, and Windows keeps the drives you connected in Explorer away from a program started that way, so the folder tree showed nothing but C:. The setup now hands the start over to your own account.

- **A model you download works right away**, without restarting FerrumPix.

- **A greyed-out tool says why.** The hint about the missing model file now appears on the tool itself.

- **Warping works on a cropped picture.** Its handles sat on the edges of the uncropped image and were out of reach; they now sit on the picture you see.
