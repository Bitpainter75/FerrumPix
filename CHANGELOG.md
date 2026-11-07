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

### Fixes

- Picking a person or place after a keyword and then clearing the selection now returns to the
  folder you started from, and returning from a filtered list keeps the ratings, labels and other
  filters you had set.
