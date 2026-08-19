## FerrumPix 0.9.30

### What's new

- **Your copyright can go into the pictures.** "Set copyright" in the metadata menu writes a notice into the selected pictures, and every batch dialog carries a field for it as well, so pictures leave the house already marked. An empty field never changes anything. A JPEG gets the notice inside the file, where it travels with it; everything else gets it in the sidecar next to it, and a RAW file is never touched. Where a picture carries a notice, the info panel shows it.

- **RAW development on Windows is three to four times faster.** A picture that used to take a couple of seconds now takes well under one, and it looks exactly the same as before. RAW development also works on machines that never had the Microsoft Visual C++ runtime installed, where it used to fall back to the small preview stored inside the file without saying so. DNG files from an HDR merge open properly now as well.

- **Windows on ARM develops RAW files at last.** Those downloads never carried the RAW engine, so they quietly showed the small preview stored inside the file instead of the real picture. They now develop RAW like every other build.

- **The file name at the top tells you whether a RAW is really developed.** In the editor it is shown in the accent colour while you work on the developed picture, and stays plain when only the small preview stored inside the RAW is on screen.

- **Stars, hearts and colour labels straight from the keyboard.** The keys 1 to 5 set the rating and 0 takes it away; SHIFT together with 1 to 9 sets a colour label, SHIFT+0 removes it; the full stop or the comma marks a favourite, so the whole thing sits under one thumb on the number pad. All of it works on the selected pictures in the gallery and on the picture in front of you in the viewer and in the editor. The commands you reach for while working now answer to a bare letter as well, not only with CTRL held: in the gallery and the viewer R, D, T, W and P for resize, convert, export, filter and print, plus I for the info panel and E for the editor; in the editor R, T, B, M, I, K and E for the tools. Where you can type, a key is still just a key. Fitting the picture into the window has moved from 0 to F.

- **A batch job can be stopped, and it says where it is.** Enlarging or denoising with a model takes minutes per picture, and until now all you got was a still screen. The wait panel counts the pictures and the tiles inside the one it is working on, and it carries a button to stop the run. The picture it was working on is not written, so nothing half done ends up in the target folder.

### Fixes

- **The selection brush shows what it really paints.** While you were painting, the marching ants ran around the outside of the stroke, so a loop or a curved sweep looked like a filled area and the selection changed shape the moment you let go. The preview now follows the painted band itself, holes included, and looks the same before and after you release.

- **Converting now gives you the same picture as exporting.** Converting was the one batch job that threw away an edit you had already made and always developed a RAW at full strength, whatever you had set. A developed RAW came back out of it plain, and on a phone DNG it came out far too dark. It now follows the same rules as "export to", including the question about denoising and object removal.

- **A picture from Immich or Nextcloud keeps its name in the editor.** It used to arrive under the name of the working copy, which is the asset id on Immich and the file id in front of the name on Nextcloud. That name sat in the footer, in the info panel and in the suggestion when you saved. Going back to the viewer also left you with that one picture instead of the album you came from.

- **A rating or a keyword that the server refuses no longer looks saved.** Stars, hearts and keywords on an Immich picture were sent off without ever being checked, so the panel showed them as stored even when nothing arrived. They now go back to the value the server has, and the footer says what went wrong.

- **An Immich picture edited elsewhere gets a fresh thumbnail.** Its tile kept the old image for good, because the cached file was named after the picture alone and never after its version.

- **The quick preview shows the picture you asked for.** Open one, close it, open the next quickly, and the first one could still appear over the second once it had finished loading. It now stays on the picture you last asked for, gives the loaded image back to memory when you close it, and shows Immich and Nextcloud pictures at full size in the gallery as well, not just in the filmstrip.

- **Double-clicking a slider takes it back to its starting value, not to its lowest one.** On sliders whose range does not pass through zero the gesture used to drop them to the bottom of the scale: JPEG quality to 1, thumbnails to their smallest size, the slideshow to one second. They now return to the value they began with.

- **Smaller things.** A lens you pick by hand now says so when the choice cannot be stored, an enlargement that falls back to another model writes it into the log, hiding a mask component no longer leaves an empty undo step, and a saved denoise pass with an unknown model is left alone instead of being redone with the wrong one.

