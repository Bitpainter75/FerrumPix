## FerrumPix 0.9.36

### What's new

- **Geometry is now a sequence of editable steps.** Crop, rotate, straighten, perspective,
  grid and line warps, resize and canvas size keep the order in which you used them. You can
  return to an earlier crop in RAW, PSD and `.fpx` projects, pull its frame out again, and keep
  working instead of relying on history alone. Geometry recipes are also much smaller: each step
  stores only what it needs.

- **Crop, rotate and straighten now live together under Transform.** Perspective, grid, lines
  and envelope distortion have their own Warp tool. Each group has an explicit Apply action where
  needed, so an unfinished adjustment cannot quietly become part of the recipe.

- **A path can become a mask layer or a text layer.** Turn a selected path directly into a new,
  independent mask layer, or turn it into editable text on that path without leaving a duplicate
  path layer behind. Text can also be inverted to the other side of any path, including free paths
  and text watermarks.

- **Fullscreen from the editor shows your current work.** It now opens a safe snapshot of the
  rendered editor scene, including unsaved adjustments, rather than reopening the original file.

- **Keywords stored inside your photos can be searched.** FerrumPix now reads the keywords written
  into a picture itself, not only those in a sidecar file, so an archive tagged in Photoshop, Bridge
  or Lightroom can be searched by keyword straight away. The background scan picks up both kinds, so
  keywords are found across your whole collection instead of only in folders you have opened. An
  existing catalogue takes them in the next time that scan runs over it.

- **You can load all shot data from a server in one go.** Immich and Nextcloud have a new button in
  their own settings section that fetches camera, lens, focal length, ISO, aperture and keywords for
  every photo on the server, so search finds them even where you have never scrolled. It only ever
  runs on that button, never on startup, and it never removes anything. Clearing the catalogue data
  or the thumbnails of a server is a separate button next to it.

- **The accent colour can be toned down.** Settings has a colour strength next to the colour
  choice, in five steps from 100 to 0 per cent. At 0 the highlight is still there, just without
  colour: it turns into a grey of the same brightness, so nothing loses its contrast.

- **Five more interface languages.** Korean, Indonesian, Turkish, Thai and Hindi are available
  in Settings. Text rendering also asks the operating system for a suitable fallback font when a
  chosen font does not contain a character.

### Fixes

- **Renaming or moving keeps your ratings, labels and keywords.** They are tied to the path, and
  until now renaming a folder or a file quietly lost all of them, along with the people you had
  named. Thumbnails move along too, so a renamed folder no longer has to be rendered again. If you
  renamed or moved a folder outside FerrumPix, Settings now has "New location for this folder" on
  the folder itself to bring everything across.

- **Enlarge canvas automatically stays on for the next rotation.** The setting was used up by the
  first Apply, so a second rotation ran without it and cut off the corners.

- **Immich photos can be searched by focal length, lens and shutter speed.** Those values were
  already in what the server sends, but FerrumPix did not keep them, so a condition on focal length
  never matched an Immich photo.

- **Nextcloud remembers what it has already looked up.** Keywords, size and capture time of a photo
  were fetched again in every session, one request per picture. They are now kept locally like the
  Immich ones and are there the moment you open the gallery.

- **A keyword added later in another program now arrives.** For RAW and Photoshop files, keywords
  from a sidecar could stop coming through once FerrumPix had taken them in once, so anything you
  added afterwards in Lightroom or Bridge never showed up.

- **Confirmed geometry can be reset again.** Resetting Crop, Transform, Perspective, Warp, Image
  size or Canvas size now changes the confirmed recipe steps as well as the visible controls.

- **A straightened picture stays straight.** Applying a later crop, perspective correction,
  resize or canvas change no longer drops an earlier straighten setting or its expanded canvas.

- **Path handles outside the picture can be grabbed.** They used to be cleared as if you had
  clicked beside the stage, or were clamped back to the image edge before hit testing.

- **The catalogue cleanup count avoids redundant network checks.** A completed index run now
  reuses the files it has already confirmed instead of querying their metadata location again.

- **Video playback no longer blocks the application while stopping.** libmpv stop, load and quit
  requests are queued asynchronously, and its native cleanup runs away from the UI thread. This
  covers local videos as well as Immich and Nextcloud downloads, particularly on macOS.

- **macOS also finds a Homebrew libmpv when started from Finder.** Both Apple Silicon and Intel
  Homebrew library locations are checked explicitly.

- **A few buttons and hints stayed German.** The path buttons for a mask layer and for text, their
  hints, the trackpad hint in Settings and three of the filter names now follow the chosen language.

- **A new watermark appears where its frame is.** Placing a text or image watermark read its
  anchor distances as if they were a position, so watermark and selection frame sat in different
  places until the first drag pulled them together.

- **Objects stay on the picture when you straighten or correct perspective.** A text, shape or
  drawing placed on a detail used to stay put while the picture under it tilted. It now turns with
  the picture, and perspective is carried into the object as well. Anchored watermarks and the
  frame keep sitting where they are, as before.
