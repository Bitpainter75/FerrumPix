<img src="Assets/FerrumPix_SettingsDark.png" />

# FerrumPix

FerrumPix is a desktop photo management and editing application for Linux and Windows, built with [Avalonia UI](https://avaloniaui.net/) and VB.NET. Project Website [FerrumPix.app](https://ferrumpix.app/) 

> **Status:** Active development, but already feature-rich in the core areas gallery, viewer, editor, and settings. The current focus is less on basic functionality and more on stabilization, UX refinement, and structural cleanup as the application grows.

## Features

<img src="Screenshots/Gallery.png" />

**Gallery**
- Folder-tree navigation (multiple drives on Windows), grid/list view, sorting, thumbnail caching; the folder tree auto-scrolls the selected entry into view, centered when possible
- Ratings (stars), favorites, tags, saved searches shown as a navigable tree (filters: favorite, rating, file type incl. RAW/non-RAW, subfolders)
- Extended search: combine text terms with AND/OR (e.g. `urlaub OR strand`, quoted phrases for multi-word terms), plus structured conditions on image data and EXIF (width/height, camera, ISO, aperture, focal length, date taken), combinable with a single AND/OR switch; EXIF/dimensions are read and cached on first use if not yet known
- File operations: copy/cut/paste, rename/batch rename, duplicate, create folder, delete (with confirmation), batch selection, reveal in file manager, copy path
- Batch rename: pattern-based with counters (`#`, `###`), and placeholders for the original name/extension, file date, EXIF date taken, image width/height, camera, ISO, aperture, and focal length; the last-used pattern is remembered between sessions
- Batch format conversion: convert selected images to JPG/PNG/WEBP (with quality setting) into a selectable target folder, with the last target folder remembered and auto-numbering on name collisions
- Further batch operations on the selection: resize (target size or scale percentage, with lock-aspect and interpolation choice), apply a saved watermark preset, and strip all metadata from local files
- Collage creation: Grid, Hero (one large image + the others framing it — top/bottom/left/right/center, position pickable via the same anchor-grid as the editor's canvas tool, or by clicking the desired image in the live preview), and Random (jittered size/rotation per photo) layouts; adjustable width/columns/margin, a per-image border, background color/format/quality, a zoomable/pannable preview with a fit button, and a reshuffle button that randomizes image order (and, in Random mode, size/rotation) across all three layouts
- Camera RAW support (CR2, CR3, NEF, ARW, DNG, PEF, RW2) alongside standard formats, plus SVG and ICO previews
- SQLite-backed library (metadata, ratings, tags, cached EXIF/dimensions for search)
- EXIF display (via MetadataExtractor)
- Video files (MP4, MOV, MKV, AVI, WebM, M4V) show a poster-frame thumbnail with a play badge

<img src="Screenshots/Viewer.png" />

**Viewer**
- Fullscreen view with fast switching between images, zoom/pan, rotate/flip
- Slideshow with configurable interval, filmstrip navigation
- Inline video playback (play/pause, seek, mute) in both windowed and fullscreen mode
- Rate, favorite, tag, and delete images directly from the viewer; jump straight into the editor
- Info sidebar with General/EXIF/IPTC/XMP tabs and a live histogram

<img src="Screenshots/Editor_Crop.png" />

<img src="Screenshots/Editor_Resize.png" />

**Editor** (non-destructive, with undo/redo)
- Crop (with presets), image resize, rotate/straighten (with auto canvas expand), flip, and canvas resize with anchor picker
- Adjust: exposure, brightness, contrast, highlights/shadows, whites/blacks, tone curve (RGB, luminance, and the individual red/green/blue channels)
- Color: white balance, temperature/tint, vibrance/saturation, split toning, and an 8-band HSL color mixer — pick a color band on a color wheel, then dial in its hue/saturation with a shared pair of sliders
- Filters: built-in filter presets with a strength slider, plus Lightroom XMP preset import and `.cube` LUT support
- Details: clarity, sharpening, softening/noise reduction (Gaussian/median), structure, haze, glow, grain/noise, and dust/scratch style effects
- Effects/Frame: vignette (size, transition, roundness, feather, freely placeable center) and border/frame controls with six edge styles (solid, dashed, jagged, double, dotted, wavy), color picker, and rounded-corner support
- Paint tool: brush, eraser, blur, and clone stamp, each with size/hardness/opacity; the stamp takes its source from an Alt+click and keeps the offset for the whole stroke, with a dashed ring marking the sampling point
- Selection tool with four modes (rectangle, ellipse, freehand lasso, magic wand): draw or click a selection on the image, then copy it into a new movable object (also via Ctrl+C/Ctrl+V, repeatable paste) or fill it — the rectangle supports a solid color or linear/radial gradient (direction/invert), irregular shapes are filled with a solid color; the magic wand selects a contiguous color area with an adjustable tolerance
- Insert objects: text, watermark, shapes (rectangle, ellipse, square, triangle, cone, pyramid, trapezoid, diamond, spiral, droplet, speech bubble, line, arrow), symbols/SVGs, images, and QR codes
- Per-object properties: fill (solid or gradient), stroke color/width, opacity, rotation, position/size, anchor handling, plus separate shadow and glow controls (color, offset, blur, strength, corner radius where applicable) — edited live directly on the canvas or via the sliders
- Watermarks can be stored as named presets (text or image, with anchor, offset, size, rotation, opacity, font, and color) and reapplied later, including as a batch operation from the gallery
- Side panel with three tabs: the active tool, the object list (reorder front/back, duplicate, show/hide, delete; drag-handles for move/resize/rotate on canvas), and a running history of the applied steps
- Color mixer at the top of the insert panel: a color wheel with saturation/brightness field, overlapping fill and background swatches with a swap gesture, the stroke color beside them, plus opacity, hex input, shared recent colors, and an eyedropper that samples straight from the image into whichever swatch is active
- Info sidebar with the same General/EXIF/IPTC/XMP tabs and live histogram as the viewer
- Before/after comparison slider

<img src="Screenshots/Editor_Text.png" />

**Settings**
- Theme (light/dark/darkgrey/lightgrey) and accent color
- Language: auto-detect, German, English, Spanish, French, Italian
- Thumbnail size/quality, JPEG export quality, filmstrip visibility, and other per-view preferences
- Preserve original EXIF/XMP metadata when using "save as", export, and format conversion (on by default); the last target folder for save/convert workflows is remembered separately, defaulting to the last gallery folder
- UI scale and font scale (whole steps, applied without a restart), video hardware acceleration toggle, transparency background (checkerboard or solid color), startup folder/image behavior, hidden folders
- Thumbnail cache management: size limit, per-folder or full cache cleanup, database cleanup
- Optional diagnostic log for preview/playback errors, written to `%LocalAppData%/FerrumPix/logs/diagnostics.log`
- Window position/size and last-used folder are remembered between sessions

<img src="Screenshots/Settings.png" />

**Immich integration**
- Connect a self-hosted [Immich](https://immich.app/) server (server URL + API key, with a connection test in Settings → Integration); a dedicated *Immich* section appears in the gallery's navigation panel with an *All photos* entry and one node per album
- Browse albums (or the whole timeline) with thumbnails served from Immich and cached locally, loaded in pages so photos appear as they arrive; open photos fullscreen with the **whole album in the filmstrip** (originals downloaded on demand, videos play inline)
- Ratings, favorites, and keywords sync both ways — set them in the gallery or viewer and they're written back to Immich; favorites use Immich's native flag, while ratings and keywords can optionally be stored in the photo description for servers where native rating/tag metadata is not surfaced reliably
- Search lists can target Immich (source switch in the search dialog, shown when Immich is configured): semantic (CLIP) search when Smart Search is enabled, with automatic metadata/client-side fallback when it is not; filename, description, tags, favorite, rating, and year-style queries are supported
- Create and rename Immich albums, and upload local images/videos to Immich (or straight into an album), from the Immich section's context menu
- Move photos between local folders and Immich in both directions via copy/paste and drag&drop: drop/paste local files onto an Immich node to upload, drag/copy Immich photos onto a local folder to download the originals
- Edit Immich photos in the editor in **save-as-only** mode — the original is never overwritten; save the result as a new local file in a selectable folder, or (via the save-as *destination* switch) upload it back to Immich as a new asset, into the source album when applicable
- Batch convert, resize, and watermark operations on Immich photos create new Immich assets instead of overwriting the original; when started from an album, the new assets are assigned back to that album, and FerrumPix waits briefly for Immich thumbnails before refreshing
- Local caching keeps large libraries responsive: thumbnails on disk, asset metadata in a dedicated SQLite index (invalidated by Immich's `updatedAt`), streamed into the grid in pages with viewport-first metadata loading; a *Clear cache* button in Settings empties both
- Current Immich limits: the editor still initializes its rating/favorite/tag fields from the local temp/catalog view when opening an Immich photo, but changes are written back to Immich; native delete/rename operations for individual Immich assets are not implemented yet
- Direct Immich integration (no third-party dependency), verified against Immich v3

## Technology Stack

- [Avalonia UI](https://avaloniaui.net/) 11.3 (Fluent theme) — cross-platform UI framework
- VB.NET on .NET 10
- [ReactiveUI](https://www.reactiveui.net/) for MVVM
- [SkiaSharp](https://github.com/mono/SkiaSharp) for image processing/rendering
- [Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia) for rendering SVG icons and SVG objects
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) for the library
- [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) for EXIF data
- [QRCoder](https://github.com/codebude/QRCoder) for QR code objects
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) for video thumbnail extraction and playback
- [Tabler-Icons](https://github.com/tabler/tabler-icons) A set of 6146 free MIT-licensed high-quality SVG icons

## Installation

All packages are self-contained — they bundle the .NET runtime, so no separate .NET installation is required to run them.

> **Video playback on Linux:** requires VLC (or at least `libvlc`) to be installed system-wide, e.g. `sudo apt install vlc` or `sudo pacman -S vlc` — it cannot be bundled into the AppImage/Flatpak the same way the Windows build bundles it. Without it, FerrumPix still runs normally; video files just won't show a thumbnail or play.


## Building from Source

Compiling the project (as opposed to just running a pre-built package) requires the [.NET SDK 10](https://dotnet.microsoft.com/) or newer.

```bash
dotnet build FerrumPix.sln
dotnet run --project FerrumPix.vbproj
```

## License

[GPL-3.0](LICENSE)
