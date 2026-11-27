## 0.9.92

### Changes

- Adding haze in the editor now tints the haze with the colour of the light in the picture, so a
  sunset gets a warm haze instead of always the same cool grey. This applies to new edits; pictures
  you edited before look exactly as they did.

- Grain added in the editor is now soft and film-like: it is strongest in the midtones, fades
  toward black and white, and its size follows the picture, so the preview and exports in any size
  show the same grain. This applies to new edits; pictures you edited before look exactly as they
  did.

- Lowering highlights on a raw picture you have brightened with exposure no longer turns blown
  out skies into a flat grey: the highlights slider holds back a little more the further the
  exposure goes up, so bright areas stay bright. This applies to new edits;
  pictures you edited before look exactly as they did.

- The editor's tool bar can now stand on the left, on the right, or on the side of the adjustments
  panel (Settings, Editor). A new automatic mode keeps it narrow and opens it over the picture when
  the pointer rests on it, without taking room from the picture. The collapse button stays where it
  was and pins the bar as before; out of the box the bar is on the left with icons and text, as it
  always was.

- The buttons at the bottom of the adjustments panel that step to the previous and next tool now
  show the icon of that tool next to its name, the same one as in the tool bar.

- Save adjustment as XMP preset or LUT. The Save adjustment dialog can now also write your
  look as an XMP preset that other raw developers read, and that FerrumPix reads back completely,
  or as a .cube LUT for video editors and other programs, with 17, 33 or 65 grid points. Sliders a
  target can't carry are grayed out, and the dialog says why.

- Spot removal from Lightroom now comes along when a raw picture's Lightroom settings are taken
  over, from a sidecar file or from a Lightroom catalog: clone spots keep their source, heal spots
  are filled by FerrumPix's own repair. Red eye corrections are not taken over yet. Presets never
  bring spots with them, since a dust spot belongs to one picture only.

- All of FerrumPix's data now lives in one data folder that you can move, for example to a second
  drive or a network share (Settings, Catalog): the catalogue, thumbnails, models, extensions,
  saved adjustments, favourites and search lists all move along at the next start, with a small
  window showing the progress until the move is done. If a second
  computer opens the same catalogue, it says which computer is using it and carries on without it.
  A new size limit for thumbnails, unlimited out of the box, removes the ones you have not looked
  at for the longest time once it is reached (Settings, Performance).

- On the first start of this version, models, extensions, favourites and search lists move once
  from the settings folder into the data folder. Older versions will not find the models there
  and download them again if needed.

- Manage people can now act on several groups at once: select them with the mark on their tiles
  and either add them to a person or remove them. Removing puts every face into the Unknown bin,
  just like "Not this person", after a question that says how many groups and faces are affected.

- A face that was not recognized can now be marked by hand: in the viewer, choose Mark face in the
  People tab of the info sidebar and drag a rectangle around it. The face can be named right away,
  makes suggestions like any recognized face, and stays when the pictures are scanned again.

- In the browser, pictures now travel both ways: drop files onto the page to put them into the
  folder you are looking at, or onto the picture in the editor to add them as a layer, and choose
  Download in the context menu or the download button on a gallery tile to bring pictures to your
  device, several at once as a ZIP with their sidecar files. Save as, Export to and the batch tools have Download as a destination, so
  the finished pictures come straight to your device without a folder on the server, and Upload in
  the gallery opens the file picker of your browser. Signing out works through Lock, and the page
  shows the FerrumPix icon in the tab. The picture now follows a slider more smoothly in the
  browser, and the app needs less processor time while nothing happens.

- FerrumPix in the browser is now also available as a container image for Docker and Podman, for
  amd64 and arm64, so it can run on a home server without a screen.

- FerrumPix in the browser can now also be used without HTTPS inside your home network, if you
  switch it on on purpose: it then only answers devices at home, needs a password, and the page
  shows that the connection is not encrypted. HTTPS stays the recommended way.

- FerrumPix can now be protected with a password (Settings, General, Access). A sign-in window
  then covers the app at start, after a time without use that you choose, and whenever you lock
  it by hand, also with the padlock at the top right of the window. In the browser the same sign-in window replaces the
  browser's own password prompt, each browser stays signed in until the app is locked, and a
  container started with a password is protected from the first start.

- **Own fonts.** Fonts you put into the Fonts folder of the data folder can be chosen for text in
  the editor next to the system fonts, and they come along into exports, prints and Photoshop
  files. Settings, Catalog shows where the folder is and reads it again on request; in the
  container it is /data/share/FerrumPix/Fonts.

- With extensions on a picture, sliders that come after an extension in the editor, such as grain
  or the settings of a later extension, now react noticeably faster, because that extension is no
  longer calculated again when nothing before it has changed.

### Fixes

- The People menu in the gallery now says when named people are missing because they don't appear
  in the open folder, and offers to show all people of the catalog. Before, someone named in
  another folder simply wasn't there, which looked as if the name had been lost.

- Olympus, OM System and Panasonic pictures taken with the camera's own shading compensation
  switched on no longer get their corners brightened a second time by the lens profile. Distortion
  and colour fringe correction stay as before.

- A damaged or deliberately crafted JPEG that claims to be far larger than its data can hold is no
  longer opened; before, a file of a few hundred bytes could make the app reserve gigabytes of
  memory.

- A LUT that an edit refers to is only read if it is an ordinary file of sensible size. An edit
  from somewhere else can no longer make the app read a device, hang on a special file, or contact
  a network share that is not in your own LUT list.

- An older FerrumPix, for example on a second computer or a Flathub package that is behind,
  no longer throws away what a newer one saved with an edit. The newer settings are kept when you
  rate, label or edit the picture. Older versions released before this one show such a picture
  unedited instead of overwriting it.

- On Windows, the AI model runtime no longer registers its telemetry provider with the system at
  all. Before, it was only switched off after the runtime had already started.

- Presets and sidecar files from Lightroom 3 and older, and edits imported from an old Lightroom
  catalog, now come in with the right exposure, contrast, highlights, shadows, clarity and tone
  curve. They are converted the way Lightroom itself updates them to its current process. Old
  brightness values in newer presets are no longer applied on top.

- EXR files are read with an updated library that closes a security issue with damaged files.

- Videos that start with a black frame get a real picture as their tile again, and the viewer
  shows that picture until you press play instead of a black area. If the picture of a video does
  not come up when it is opened, the viewer now tries once more by itself.

