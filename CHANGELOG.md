## Unreleased

### Changes

- Keywords can be arranged under one another, for example Dogs and Cats under Animals. The new
  button "Arrange keywords" at the bottom of the folder panel opens a dialog in which a keyword is
  dragged onto another one. The keyword list of the gallery then shows them as a tree, and
  choosing Animals also finds the pictures that only carry Dogs. Typing "Animals > Dogs" into the
  keyword field arranges a keyword while assigning it, and keyword paths from other programs are
  taken over as this order. The pictures themselves keep their plain keywords.

- TIFF files with 16 bit per channel, such as scans, now keep their depth: Save as, export and the
  batch dialogs offer 16 bit for them as for raw files, and the adjustments are calculated at full
  depth. Colours of such a file with an embedded colour profile are also read more accurately.

- The brush for selections and masks can stop at edges. With the new option "Stop at edges" a
  stroke only covers what resembles the colour under the centre of the brush, so painting along a
  sky or a jacket no longer runs over the edge. A tolerance sets how strict it is, and the result
  appears when the stroke is released.

- Two new erasers in the image brush. The background eraser removes under the brush only the
  colour on which the stroke starts, the magic eraser removes an area of similar colour
  with one click. Both work in the photo and on a picture layer.

- The Export button has moved from the bar at the bottom of the gallery into the folder panel,
  below Import.

- The dialogs for exporting, importing, Save as, resizing, converting, filters and watermarks are
  wider.

- On a small screen the scrollbar of the tool bar in the editor lay over the tools. It is gone:
  the mouse wheel still scrolls the bar, and a small arrow at the top and at the bottom shows
  that there are more tools in that direction.

### Fixes

- FerrumPix could close without warning while a RAW file was being developed, mostly after it
  had been idle for a moment. This no longer happens.

- A crop that was also rotated arrived wrong when it was taken over from an XMP file: the
  rotation was dropped and the frame sat in the wrong place. Both now arrive as they were set.

- Typing part of a name at a face and then clicking the full name in the list of suggestions
  assigned the typed part, for example "Dad" instead of "Daddy (me)", and further steps could
  rename the whole person. The clicked suggestion now counts. Before a name typed at one face
  renames a whole person with several faces or removes their name, FerrumPix asks.

- A single face could not be assigned from the people area: the hint at the unknown bin pointed to
  the info sidebar, which the preview behind the eye does not have. The preview now has a name
  field with suggestions that assigns exactly this face.

- The scrollbar in the dialogs no longer lies over the right edge of the fields. In the export
  dialog it covered the arrow of the dropdowns, which made them hard to hit. It now has its own
  strip beside the content.

- When the trash of the system refuses a file, for example on a second drive that has no trash,
  FerrumPix now shows the reason the system gives and asks whether to delete the file for good
  instead. Before, it only said that the trash was not available, and in German in every language.

- With the keyword list of the gallery open, clearing the selection left only the keywords of the
  pictures shown before in the list. The list is complete again.


