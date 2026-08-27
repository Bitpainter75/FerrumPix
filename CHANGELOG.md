## FerrumPix 0.9.33

### What's new

- **Setting the text size is a matter of one click.** The slider moved a whole step per pixel of mouse travel, and the interface resized while you dragged it, so the control wandered away under your hand. There are eight buttons now, one per step, each showing "aA" in the size it sets, with the current one highlighted. They all stand on one line, so you can see the step you are choosing.

- **Windows: HEIC and HEIF photos open straight away.** The pictures your phone takes needed a library you had to find and place next to FerrumPix yourself, and most downloads of it did not work because they expect further files beside them. That library now comes with the Windows download, so those photos open in the viewer and the editor instead of showing up as thumbnails only. On Linux it keeps coming from your package manager, and macOS reads these files itself.

### Fixes

- **The sorting menu highlights the entry under your pointer.** It always tried to, but with exactly the colour of the menu behind it, so the highlight painted itself invisible. The same applies to the other menus built that way.

- **The entry under your pointer has rounded corners in every menu.** In the filter menus it always had, in the context menus it was a square block.

- **Submenus follow the theme you picked.** A submenu is a window of its own, and it kept the colours of the dark theme after a switch to one of the grey themes or to the light one, so it stood out against the menu it belongs to. The drop-down of a selection and the rows of a list had the same problem.

- **Text fields that are switched off look like text fields.** Where a setting has its own switch, as with Immich and Nextcloud, the fields below it were framed by a pale block instead of their usual outline.

- **The folder a picture lives in is shown in the information sidebar**, under its file name. Pictures from an Immich or Nextcloud server do not have one, so the line stays away there.

- **Save and Save as keep their labels out of each other's way.** With the adjustment panel on the left, the buttons in the middle of the editor's top bar move right along with the picture, and in a narrow window they ran into Save and Save as. The labels now give way earlier when the panel sits on the left.

- **Photos marked as Adobe RGB only in their EXIF now arrive in the right colours.** Some cameras write no colour profile into the file and say Adobe RGB in two metadata fields instead. Those photos looked flat and shifted, most visibly in saturated reds and greens. A profile in the file still wins over the metadata.

- **Photoshop layers keep the colours of the document.** The flattened image was already converted, the layers were not, so a file in Adobe RGB fell apart into a correct background and shifted layers on top.

- **A cut piece of the picture does not stay behind.** Cutting a selection and pasting it right away showed the piece twice, once as the pasted layer and once where it had been, until the editor happened to redraw the whole picture.

- **Save as offers the folder the picture came from**, also when you opened the picture straight from the file manager or the command line and went on to the editor. There is no gallery behind that, so the dialog fell back to the folder you saved into last, and your picture went somewhere else than where it lives.

- **The bar at the top of the window is slimmer.** It is a third lower than before, so the picture gets the room instead. Logo and window buttons came down with it.

- **Smaller touches in the gallery.** The parent folder in the path bar has room around its highlight again, and the free space line below the folder tree is one step larger, which makes it readable.

- **Windows: installing and uninstalling no longer leaves a half-removed program behind.** Windows keeps a running program locked, so uninstalling while FerrumPix was open removed everything except the program itself, and updating over a running FerrumPix stopped in the middle of copying. Both now say that FerrumPix is still open and wait until you have closed it.

- **Windows: FerrumPix is in the Start menu after installing.** If the installation asked for an administrator password, the entry was put into that administrator's Start menu and was missing from yours. The setup now creates it for everyone on the computer, and it tidies up a stray entry left behind by an earlier installation.
