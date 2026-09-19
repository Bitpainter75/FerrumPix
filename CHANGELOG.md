## Unreleased

### What's new

- **Colour labels while comparing two pictures.** Both sides of the comparison view now carry the
  same row of colour labels you know from the info panel, so you can mark the keeper without
  leaving the comparison.

- **A margin when fitting a picture into the editor.** The settings now let you keep a gap of up to
  200 pixels between the fitted picture and the edge of the editing area, instead of the picture
  sitting flush against the interface. Zero keeps the previous look.

### Fixes

- Switching between mask layers inside an adjustment tool is more than ten times faster. On a
  picture that has been straightened, turned and cropped, the mask was worked back through that
  chain point by point on a single core, on every single switch - even when you had changed
  nothing. It now only happens when the mask really was edited.

- Picking a colour range or a brightness range as a selection is several times faster on large
  photos, so the sliders keep up while you drag them.

- Working with mask layers is faster throughout: building a mask, showing it as the red overlay and
  the brush correction on top of it all got several times quicker on large photos.

- Depth blur takes about half as long.

- Clicking a mask layer now puts you into the mask tool. You were left holding the selection tool
  while looking at the mask. Adjustment tools are the exception: there the layer is simply what the
  sliders act on, so you stay where you are. A selection layer keeps the selection tool as before.

- The mask button on a layer now takes you into the mask tool, the same way it already did on an
  object. It opened the mask but left you in whichever tool you were in.

- Menus, tooltips, flyouts and drop-down lists now use the same font as the rest of the
  application. They used to fall back to the system font, and a damaged system font could close
  FerrumPix the moment a tooltip appeared.

- A watermark placed on a straightened picture no longer comes out tilted. The selection box sat
  where you put it while the watermark itself was turned by the straightening angle.

- A watermark anchored to a corner now stays on that corner of a cropped or turned picture. It and
  its selection frame could end up in different places, both when you placed it and after you
  dragged it.

- Moving an object while zoomed in no longer makes it jump back to where it was. The sharp detail
  view kept showing the picture from before the move once you let go.

- Lens correction is now only offered where it works. It corrects the sensor data while a RAW file
  is developed, so it never had any effect on a JPEG, TIFF or HEIC photo, even though the group
  showed the detected lens and let you move all three sliders. Those photos usually come out of the
  camera or a RAW developer with the correction already applied, and doing it a second time would
  bend the picture the other way.

- The strength sliders for distortion, colour fringes and corner darkening can only be moved when
  the lens has measurements for that correction. Before, only the tick box above them was locked.

- Choosing a lens by hand now reaches the picture. If the capture data named no lens, the panel
  showed your choice but the photo stayed as it was, while the thumbnail and the exported file were
  corrected.

- Painting with the healing brush, clone stamp or blur brush stays smooth on large pictures.
  Before, the editor became sluggish from the second stroke on.

- A clone stamp stroke on a large picture is applied many times faster after you let go.

- Pictures pasted into the gallery get their preview and their capture data, and are added to the
  catalog. Before, a pasted picture could stay without a preview.

- TIFF photos now show in the viewer, in full screen, in the quick preview you get with the SPACE
  key and in the dialog that asks about an existing file. They appeared in the gallery and opened
  in the editor, but stayed empty everywhere else. Those places also show SVG and icon files now.

- Reducing noise with a model, and upscaling, now work on every picture. On macOS they failed on
  every photo, and elsewhere on pictures with object layers.
