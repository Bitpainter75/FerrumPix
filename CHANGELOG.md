## FerrumPix 0.9.26

### What's new

- **Your own adjustment presets work over a whole selection.** A set of sliders you saved in the Adjust tool can now be picked in Apply filter and in Export to, right next to the built-in filters, LUTs and XMP presets.

- **You can give your photos a place.** Right-click a picture and pick Metadata, Set location: type a coordinate or simply a town name and it is written to the photos you selected. JPEG files get it inside the file, everything else gets it in a sidecar file next to the original, and RAW files are never touched. The town search runs on your own machine and sends nothing anywhere.

- **One photo can hand its place to the others.** If one picture of a trip has a location and the rest do not, copy it from that one and paste it onto the whole selection.

- **A location can be taken off again.** Remove location clears it from the image file, from a sidecar next to it and from the catalogue, and it asks first. The coordinates are overwritten in the file, not just hidden, so they do not travel along when you pass the picture on.

- **A picture's location can be opened on the map.** Metadata, Show location in OpenStreetMap opens the spot in your browser.

- **Nextcloud takes uploads.** Drag local pictures onto a Nextcloud entry, paste them, or pick them from the right-click menu; they go to the folder you set in Settings, and a name that is already taken is numbered instead of overwritten.

- **Nextcloud can replace the original.** With the new setting, saving writes the edit into the file on the server - it keeps its identity, its albums and its shares. Left off, only a sidecar file with the edit is created next to the original, and RAW and PSD files are never overwritten either way.

- **Delete for good on Nextcloud.** A second switch next to *Allow deleting* sends deleted photos past the trash, and the confirmation says so.

- **The filter buttons know your Nextcloud.** The people, places and keywords of the server now stand in the same lists as the local ones, under a heading with the name of the source, and a click opens that view. The buttons also appear when only a server knows people or places.

- **Keywords reach the Nextcloud server.** A keyword given in gallery, viewer or editor is written to the photo on the server instead of into a local catalogue entry nobody sees again.

- **Searching works the same everywhere.** Immich and Nextcloud now have their own Search area above the tree, with saved searches just like folders have. The dialog no longer asks where to search - that follows from the area you started in - and it is split into what you can always search for and what comes from the catalogue.

- **A server search asks the server first.** Nextcloud looks up names, people, places, keywords and favourites on the server itself; Immich adds its own ratings and camera data. Only ratings and colour labels come from the catalogue, because no server keeps them. Text finds a keyword as well as a file name.

- **Grain can be colored.** A new slider next to strength, size and roughness lets the three colour channels drift apart, from plain grey grain to the coloured speckles of a fast colour film. At zero the grain looks exactly as it did before.

- **Your whole collection can be searchable, not just the folders you opened.** Name the folders your photos live in under Settings, Catalogue, and FerrumPix reads them in the background including subfolders: shot data into the catalogue, thumbnails ready for the gallery. It can start on its own shortly after launch, there is an *Index now* button for a run on demand, and it can be stopped at any time. Later runs only look at what has changed, and nothing is ever written next to your photos.

- **One folder list instead of two.** Settings, Catalogue now shows every folder FerrumPix knows in a single list, grouped under the folders you watch, with the numbers of a whole tree added up on its top line. There is a search over the full path, and the actions apply to whatever the search leaves: clean up the catalogue data or the thumbnails, or send the people search over them. Everything is also available on a single folder, so you can search the faces of one trip without starting on the whole collection.

- **Settings have a Catalogue section of their own.** Everything about what FerrumPix knows of your collection now sits together, above Performance, which keeps the cache sizes and quality.

- **The system trash stays out of the catalogue.** Photos you threw away no longer turn up in the folder list, in search results or in places and people. *Clean database* removes the ones that got in before. Immich and Nextcloud photos no longer appear as folders either.

- **Everything that runs says so, in one place.** Indexing, both face searches and a saved search now show their progress where the search box sits, each with a button to stop it. A saved search over a big collection used to run with nothing but a quiet line at the bottom and no way to call it off. If two things run at once they stand next to each other and share the space, instead of one hiding the other.

### Fixes

- **The Metadata menu is back in the viewer and the editor.** Setting, copying, pasting and clearing a location and removing metadata were only offered in the gallery, although the menu is the same everywhere. They now work on the picture you are looking at, and the info bar shows the new place right away.

- **Search no longer shows pictures you threw away.** The folder search walked into the system trash, so deleted pictures turned up in the results - and nothing happened when you tried to delete them there. The trash is now left out, and if a delete is refused you get told why instead of silence. A saved search also forgets the thrown away pictures it remembered from earlier runs, so they stop coming back every time you open it, and giving a picture a rating, a keyword or a place no longer puts a thrown away photo back into the catalogue.

- **A picture deleted in the viewer no longer stays in the gallery.** Going back showed it as if it were still there. In a folder it disappeared once the folder was read again, but in search results it stayed until you left the view.

- **Opening a saved search does half the work it used to.** Every picture it had already found was looked up and checked a second time on the way through the folders, only to be dropped as a duplicate at the end. On a search with thousands of hits that was the whole job done twice. While it runs it now says how many pictures it has checked as well as how many it found, so a search that turns up nothing new no longer looks stuck.

- **Cleaning up the catalogue no longer forgets your server photos.** Removing orphaned entries checked every path for a file on disk. Server photos have none, so their ratings, keywords and people were thrown out with them.

- **Replacing a photo on Immich no longer loses metadata.** If the server refused the description, the rating or a keyword, that went unnoticed and the original was moved to the bin anyway. Now the original stays and says why.

- **Search results are no longer lost while a search is still running.** Saving happened in the background on a list the search kept adding to, which could fail outright or let an older save overwrite newer hits.

- **Copy folder path works reliably.** It reached for the clipboard from a background thread, so depending on the system it silently did nothing.

