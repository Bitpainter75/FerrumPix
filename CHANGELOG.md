## FerrumPix 0.9.29

### What's new

- **Photos with their own colour profile now look right.** A picture in Adobe RGB or Display P3 used to arrive as if it were sRGB, which left it flat and shifted the colours. It is now converted when it opens, in the gallery, the viewer and the editor alike. What you save is sRGB and says so, so the next program reads it the same way.

- **Waveform and RGB parade.** Two new ways to read a picture next to the histogram in the info panel: the waveform keeps the left-to-right position, so you can see whether the sky burns out on one side while the other still holds detail, and the parade puts the three colour channels side by side, which is how you spot a colour cast. A click opens the diagram large, and it follows your edits.

- **Select by colour or by brightness.** Click a colour in the picture and everything close to it is selected, or take a range of lightness instead: only the deep shadows, or everything above a certain brightness. Both are in the selection tool and in the mask tool, and both can be refined with the brush afterwards.

- **The selection tool can do much more.** The brush and the object selection are in it as well, a selection can be copied and pasted the way a mask already could, and holding CTRL while you drag keeps a rectangle square and an ellipse round.

- **The gallery and the viewer feel quick again, and FerrumPix starts leaner.** The diagram is only worked out for the panel you actually have in front of you, and the symbol list is put together the first time you reach for it.

### Fixes

- **Masks and selections keep what you gave them.** A selection that belongs to a layer stays where you moved it, a mask you have already painted is quick to pick up again, and pressing "new mask layer" a second time makes an independent copy of the shape instead of doing nothing. The range sliders work in both tools, and pulling one back makes the selection smaller again.

- **Presets arrive complete.** A preset with a sky, subject or brush mask no longer darkens the whole picture, a crop is imported without wiping the edges the preset does not mention, the colour fringe correction comes along, and a look you had loaded before is properly replaced rather than left on top.

- **Copying a path or a file copies what you pointed at.** From the context menu the copy could end up empty or take a different picture than the one under the pointer.

- **Thumbnails are made again for the colour conversion.** Tiles written before still carried the colours of their own profile and would have kept them.

- **Smaller fixes.** Switching in the tree drops the person, place and keyword filters again; the metadata badges answer in the list view too; the sort button shows the direction as an arrow; text fields in the drop-down menus no longer sit on a grey block; clicking the empty layer list drops the selection; a second picture cannot be started while one is still loading; and the colour space entry is now corrected in files that carry an extra marker in front of their shot data.
