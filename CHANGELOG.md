## Unreleased

### Changes

- **Folder or catalogue**: a new button next to the search field decides whether search, filters,
  people, places and keywords look only at the open folder or at your whole catalogue. The numbers
  in the people, places and keyword lists follow the same choice, so in a folder they only count
  what is there. The folder is the default.

- Keywords, ratings and colour labels can now also be read from RawTherapee (`.pp3`) and ART (`.arp`)
  sidecar files next to your photos. Turn it on in Settings, Catalog. The files are only read, never
  changed.

- Colour labels from darktable and colour labels, pick marks and keyword paths from digiKam are now
  picked up from their XMP sidecar files too.

- With the diagnostic log turned on, a search or a people, places or keyword selection now notes
  which catalogue entries it left out and why, for example because the file is no longer there.
  A catalogue run also notes how long it took and where the time went, which settings it ran with,
  and which entries have no file.

- Sliders in the editor follow your hand more smoothly while you drag them.

- Depth blur, the depth map, object selection and object removal finish noticeably faster on large
  photos, and thumbnails are made more quickly. The results stay exactly the same.

- Raw photos open in the editor sooner: lens distortion is corrected on the graphics card, and the
  gallery no longer prepares its histogram for the photo while the editor is still opening it.

- Moving a layer in the editor follows the mouse much more smoothly where layers with a blend
  mode are involved, and letting go no longer freezes the window for a moment.

- Raw photos without a usable built-in preview get sharper thumbnails, made faster, and image
  recognition reads them from the thumbnail instead of developing the photo again.

### Fixes

- Some thumbnails came out narrower than their tile and looked soft, for example portrait photos
  from phones. New thumbnails now always fill the tile.

- **Similar pictures** compare a finer picture of each photo now: different scenes with a similar
  layout, like sunsets on different days, no longer end up in one group, and Strict keeps shots of a
  series together more often. The pictures of a folder are measured once more the first time.

- Picking a person or place after a keyword and then clearing the selection now returns to the
  folder you started from, and returning from a filtered list keeps the ratings, labels and other
  filters you had set.
  
- A double click or a bouncing pen tap with an eyedropper in the editor no longer switches to the
  Selection tool or starts moving an object: the second press right after picking a colour is ignored.
