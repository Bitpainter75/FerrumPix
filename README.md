<img src="Assets/FerrumPix_SettingsDark.png" />

# FerrumPix

FerrumPix is a desktop photo manager and image editor for Linux and Windows, with experimental ARM64 and macOS builds. It is built with [Avalonia UI](https://avaloniaui.net/) and .NET 10.
I absolutely love VB.NET, even though it's pretty rare nowadays.

I wanted to share my private project I’ve been working on. It’s basically an application built exactly the way I always wanted it to look and function. I’m releasing it here completely free and open-source for anyone who might find it useful.

To be transparent: yes, I use AI to support my development workflow. However, anyone who actually codes knows that AI cannot build a complete, production-ready application on its own. It still requires a massive amount of manual work, architecture planning, and debugging. I am currently investing a lot of time, a ton of passion, unique ideas, and genuine hard work went into this project.

FerrumPix is in active development. The gallery, viewer, editor, settings and Immich integration are already usable. Current work focuses on stability, performance, workflow polish and cleanup.

Project website: [FerrumPix.app](https://ferrumpix.app/)


## What FerrumPix Does

- Browse local photo folders with thumbnails, ratings, favorites, tags and saved searches.
- View photos fullscreen with zoom, pan, filmstrip navigation, metadata and histogram.
- Edit photos with crop, resize, rotate, color tools, tone curves, filters, text, shapes, symbols, retouch tools, paint tools and selections.
- Run batch work from the gallery, including rename, convert, resize, watermark, metadata removal, filters and *Export to*, which combines all of those into one dialog. Every photo format FerrumPix can open works as a source — RAW, PSD, `.fpx`, HEIC, TIFF, BMP and GIF included; formats that cannot be written back simply produce new files instead of offering *Overwrite originals*. A RAW with an `.fpxmp` recipe is developed and processed the way you edited it; whether RAWs without a recipe are developed too is a setting under *RAW development*.
- Develop RAW files from the sensor data, with automatic sidecar files that keep the original untouched.
- Work with common image and video formats — JPEG, PNG, WEBP, BMP, GIF, RAW, and read-only HEIC/HEIF/AVIF, TIFF and Photoshop files.
- Connect to a self-hosted Immich server for browsing, upload, download, editing and metadata sync.

## Gallery

<img src="Screenshots/Gallery.png" />

The gallery is built for daily photo work. It supports folder browsing, fast thumbnails, file operations, ratings, favorites, tags and saved searches. Star ratings, the favourite mark and the metadata badges on each tile can be set to stay visible or to appear only on hover, each independently, in Settings.

Search can combine normal text with metadata such as camera, ISO, aperture, focal length, date taken and image size. Batch tools are available from the context menu and from the footer menu.

Ratings, colour labels and keywords are read from XMP sidecar files written by Lightroom, darktable or digiKam, so a collection you tagged elsewhere shows up here. Only empty fields are filled and keywords are merged — nothing you set in FerrumPix is overwritten.

The sidebar is split into *Folders*, *Immich* and *Favourites*. Folders, Immich entries and saved searches can be pinned to Favourites by right-click, then reordered or removed; the *Immich* tab only appears when a server is configured.

Printing works from the context menu, the footer menu or with `Ctrl+P`. A multiple selection becomes a multi-page document, or a contact sheet with 4, 9 or 16 images per page. The dialog can print borderless, and for a single selected image it can repeat the same photo several times on a sheet.

## Viewer

<img src="Screenshots/Viewer.png" />

The viewer opens photos and videos quickly and keeps navigation simple. It supports fullscreen mode, zoom, pan, slideshow, filmstrip navigation, ratings, favorites, tags and deletion.

Video files use `libmpv` for inline playback and thumbnails. Linux packages use the system `libmpv`; Windows packages bundle the mpv runtime with FerrumPix.

Printing is available from the toolbar or with `Ctrl+P`, using the same dialog as the gallery.

## Editor

<img src="Screenshots/Editor_Edit.png" />

The editor covers the most common photo work:

- Create a blank image with `Ctrl+N`: presets for photo, screen and paper sizes, free width and height in mm, cm, inches or pixels at 72–600 dpi, and a white, transparent or coloured background.
- Crop, resize, rotate, flip and canvas resize. On files that keep their edits beside the untouched original — RAW and PSD via their sidecar, `.fpx` inside the bundle — the crop stays a recipe value: the crop tool shows the whole picture, every other tool shows the crop, and it can be widened again at any time. Writable formats such as JPEG and PNG keep the *Apply crop* step, since there the crop really is written into the file.
- Exposure, brightness, contrast, highlights, shadows, tone curves and white balance.
- Color tools with HSL, vibrance, saturation, colour grading (four colour wheels for shadows, midtones, highlights and global — double-click a wheel to reset it), camera calibration and colour noise reduction.
- Automatic enhancement: *Auto* measures the photo and sets exposure, contrast, highlights, shadows, black and white point, vibrance and white balance to values that suit it. The sliders stay editable afterwards, and the filter group's reset button takes the automatic correction back out.
- Filters, LUT files and Lightroom `.xmp` preset import, including the newer colour-grading keys and black-and-white presets.
- Film negative conversion for scanned negatives.
- Text, shapes, symbols, images, QR codes and watermarks. Text can be set bold or italic, spaced out, and placed along an arc, a circle or a wave.
- Brush, transparent eraser, blur/smudge, clone stamp and repair brush tools. The brush picker offers 13 variants — soft round, pencil, marker, grainy acrylic, sandpaper, smudge, spatter, charcoal, crayon, airbrush, calligraphy, stipple and watercolor.
- Rectangle, ellipse, lasso and magic wand selections. Selections are shown as marching ants, masks as a red overlay.
- A separate mask tool with a mask brush (adjustable soft edge, add/subtract painting) and two masks you drag onto the image: a linear gradient that runs a correction out along the drag direction, and a radial one that fades it from a centre, invertible so it acts outside instead of inside. Gradient masks are stored as their geometry, not as painted pixels, so their handles, transition width, angle and — for the radial one — its ellipse can be changed at any time.
- Copy the adjustments of one image (Adjust, Colour, Details, Effects) and paste them onto another; crop, size, objects and masks are deliberately left out. The copy survives restarts.
- Turn a selection or mask into a correction layer whose adjustment applies only inside it. The fill tool fills a selection layer with a solid colour or a linear/radial gradient; on a mask layer the fill's brightness grades how strongly the adjustment applies across the mask — and stays editable afterwards.
- Per object editing with opacity, blend modes, shadows, glow and transform controls.
- A toggleable Layers panel with the full object stack: per-layer visibility, opacity, blend mode, drag-and-drop reorder, rename (double-click or F2), rasterize (bake a layer into the image so retouching can work on its pixels) and delete, plus the base image as a hideable background layer. Selection and mask correction layers appear with their own name and icon, and every layer's actions are available from the footer or a right-click context menu. Object layers can be grouped (Ctrl+G): a group is one named, collapsible row with its own visibility switch, and picking any member selects the whole group so it moves, scales, rotates and flips as one.
- A native project format (`.fpx`): *Save as…* can bundle the whole edit — adjustments, layer stack and the baked working image — so it can be reopened and continued. Adjustments and object layers stay editable after reopening; retouching, brush strokes and rasterized layers are baked into the image (undo covers them only within the session). `.fpx` projects show up in the gallery, viewer and fullscreen like any image.

### RAW and Photoshop files

RAW files are developed from the actual sensor data — full-resolution demosaic, camera white balance, sRGB — instead of editing the embedded JPEG preview. The status bar shows whether you are working on *RAW developed* or *RAW preview*. LibRaw comes with the packages: Linux packages depend on the system library, the Flatpak builds it in, and Windows releases bundle it.

Slider edits on RAW files are remembered in a small `.fpxmp` sidecar file next to the RAW and re-applied the next time you open it. The RAW itself is never modified. Sidecars travel with the RAW when it is moved, copied, renamed or deleted in FerrumPix. If a RAW carries a Lightroom `.xmp` sidecar with develop settings, they are converted once into an `.fpxmp` recipe so the photo opens the way you left it elsewhere.

Photoshop files (`.psd`/`.psb`) open in the gallery, viewer and editor. FerrumPix reads the flattened composite and never writes them back — *Save* is disabled, *Save as…* exports to the usual formats.

HEIC/HEIF/AVIF (the format current phones photograph in) and TIFF open the same way, read-only. HEIC needs the system's `libheif`, which FerrumPix loads if it is there and does without if it is not — it is deliberately not bundled, because HEIC is usually HEVC-encoded and that decision belongs to the distribution. TIFF needs nothing extra: 8 and 16 bit, greyscale, palette, CMYK, LZW/Deflate/JPEG compression, striped and tiled files are covered (16-bit CMYK is not), and multi-page files show the first page. BMP and GIF (first frame) can be read and processed as well.

Exporting to JPEG/PNG/WEBP writes the result into pixels; while the editor is open, changes can be undone and objects stay editable. Save as a `.fpx` project (or use *Save as* to a normal image) if the original file should stay untouched.

`Ctrl+P` prints the current edit state — adjustments, objects and brush work included, not the file on disk. PDF is also available as a target format in *Save as…* and *Convert to…*; it uses the page setup last confirmed in the print dialog.

<img src="Screenshots/Editor_Text.png" />

## Immich Integration

FerrumPix can connect directly to a self-hosted [Immich](https://immich.app/) server.

Supported Immich work includes:

- Browse all photos and albums.
- View albums with the full album in the filmstrip.
- Upload local photos and videos.
- Download Immich originals to local folders.
- Sync ratings, favorites and keywords.
- Search Immich from saved search lists.
- Edit Immich photos and save the result as a new asset.
- Optionally update existing Immich assets in place.
- Optionally delete Immich photos and albums when this is enabled in Settings.

### Required API key permissions

FerrumPix authenticates with an Immich API key. A key with `all` works, but if you prefer a restricted key, these are the permissions FerrumPix actually uses. Every feature calls its own endpoint, so a missing permission disables that one function instead of breaking the whole integration — you can start narrow and widen later.

Build the key up in layers, depending on how much you want FerrumPix to do:

**Read-only — browse, view, download:**

```
user.read  asset.read  asset.view  asset.download  album.read  person.read  tag.read
```

**Add for writing metadata** — ratings, favorites, description, keywords:

```
asset.update  tag.create  tag.asset
```

**Add for uploading** — upload from the gallery, and saving an edited photo as a new asset:

```
asset.upload  albumAsset.create  asset.copy
```

Plus `album.create` and `album.update` if FerrumPix should be able to create and rename albums.

**Add for deleting** — only needed when *Allow deleting* is enabled in Settings:

```
asset.delete  album.delete
```

The full mapping, in case you want to know what each one is actually for:

| What it enables | Immich endpoint | Permission |
| --- | --- | --- |
| Connection test in Settings | `GET /users/me` | `user.read` |
| Browsing photos, search lists, places, people counts | `POST /search/metadata`, `POST /search/smart`, `GET /search/explore`, `GET /search/cities` | `asset.read` |
| Thumbnails in gallery and filmstrip | `GET /assets/{id}/thumbnail` | `asset.view` |
| Opening and downloading originals | `GET /assets/{id}/original` | `asset.download` |
| Metadata of a single photo | `GET /assets/{id}` | `asset.read` |
| Albums as virtual folders | `GET /albums` | `album.read` |
| People as virtual folders | `GET /people` | `person.read` |
| Reading keywords | `GET /tags` | `tag.read` |
| Sync of ratings, favorites and description | `PUT /assets/{id}` | `asset.update` |
| Writing keywords | `PUT /tags`, `PUT`/`DELETE /tags/{id}/assets` | `tag.create`, `tag.asset` |
| Upload from the gallery, and saving an edited photo as a new asset | `POST /assets` | `asset.upload` |
| Putting an uploaded photo into an album | `PUT /albums/{id}/assets` | `albumAsset.create` |
| Creating an album | `POST /albums` | `album.create` |
| Renaming an album | `PATCH /albums/{id}` | `album.update` |
| Deleting an album (Settings → *Allow deleting*) | `DELETE /albums/{id}` | `album.delete` |
| Deleting photos (Settings → *Allow deleting*) | `DELETE /assets` | `asset.delete` |
| Carrying albums, favorite, stack and shared links over to a replaced asset | `PUT /assets/copy` | `asset.copy` |
| *Update existing assets* — writing an edit back onto the original asset | `PUT /assets/{id}/original` | see note below |

Note on *Update existing assets*: that option replaces the file of an existing asset. The permission guarding this endpoint could not be confirmed against the current Immich source, and it has changed names across versions. If the option is enabled and saving fails with HTTP 403, check the permission list in your Immich version's API key dialog for the entry covering asset replacement — or leave the option off, in which case FerrumPix always creates a new asset and this endpoint is never called.

Permission names come from the Immich server source and apply to reasonably recent versions; older servers with a single all-access key are unaffected.

## Settings

<img src="Screenshots/Settings.png" />

Settings cover theme, accent color, language, thumbnail quality, export quality, metadata handling, video support, UI scale, font scale, cache cleanup and Immich connection details.

The last two sections are reference material: a full list of keyboard and mouse shortcuts for gallery, viewer and editor, and a *Technology* section listing everything FerrumPix is built on, with a link to each project and to its licence text.

## Technology

- [Avalonia UI](https://avaloniaui.net/) 12.1
- VB.NET on .NET 10
- [ReactiveUI](https://www.reactiveui.net/)
- [SkiaSharp](https://github.com/mono/SkiaSharp)
- [Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia)
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/)
- [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet)
- [QRCoder](https://github.com/codebude/QRCoder)
- [libmpv](https://mpv.io/)
- [LibRaw](https://www.libraw.org/) (RAW development)
- [Tabler Icons](https://github.com/tabler/tabler-icons)

## Installation

Release packaging targets Linux and Windows:

- Linux AppImage and Flatpak
- Debian/Ubuntu package (`.deb`)
- Fedora/openSUSE package (`.rpm`)
- Windows Setup
- Portable Linux ZIP
- Portable Windows ZIP

Experimental, untested builds — feedback is welcome:

- ARM64
- macOS

And as a package in the AUR:

- https://aur.archlinux.org/packages/ferrumpix-bin


The packages are self-contained and include the .NET runtime.

`libmpv` (video playback and thumbnails) and `libraw` (RAW development) are required, not optional. The Linux packages declare both as dependencies, so the package manager installs them along with FerrumPix. Windows releases bundle both under `runtimes/win-x64/native`, in the setup as well as in the portable ZIP.

Two cases differ. The Flatpak builds LibRaw into the sandbox but deliberately ships no `libmpv`, so it has no video support. The Linux ZIP and the AppImage have no package manager to pull anything in and expect both libraries on the system; the experimental macOS builds ship without LibRaw — install it with `brew install libraw`.

Where a library is present on the system, FerrumPix uses that one in preference to a bundled copy, so it keeps getting security updates and support for newer cameras. Where one is genuinely missing, FerrumPix keeps running: video files are then unavailable and RAW files fall back to their embedded preview.

## Building From Source

Building FerrumPix requires the [.NET SDK 10](https://dotnet.microsoft.com/) or newer.

```bash
dotnet build FerrumPix.sln
dotnet run --project FerrumPix.vbproj
```

## License

[GPL-3.0](LICENSE)
