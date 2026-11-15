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

- The Export button has moved from the bar at the bottom of the gallery into the folder panel,
  below Import.

- The dialogs for exporting, importing, Save as, resizing, converting, filters and watermarks are
  wider.

### Fixes

- When the trash of the system refuses a file, for example on a second drive that has no trash,
  FerrumPix now shows the reason the system gives and asks whether to delete the file for good
  instead. Before, it only said that the trash was not available, and in German in every language.

- With the keyword list of the gallery open, clearing the selection left only the keywords of the
  pictures shown before in the list. The list is complete again.


