## Unreleased

### What's new

- **The frame works like the other objects.** It has an on and off switch in its group, and below
  it the same fill, shadow and glow groups as text, with every option: all gradient types with as
  many color stops as you like, presets, and shadow and glow inside or outside. Radial and diamond
  gradients run along the frame itself, so every color shows. The frame can also keep a distance
  from the edge of the picture. A semi-transparent gradient frame now keeps the opacity you set
  instead of turning fainter.

- **Warp beyond the edge.** The grid, envelope and line warps let you drag the corners and edges
  of the picture past its border, like a free transform. The preview shows the part outside
  darkened, and applying keeps the picture size, so what was pulled out is cut off. Edges snap
  back onto their line when you come close, so they stay straight unless you mean otherwise.

### Fixes

- Red eye removal moved to the image brush, next to the eraser, dodge, burn, sponge and replace
  color. One click on the pupil removes the red inside the brush circle. Blur, healing brush and
  clone stamp no longer carry it.

- In the selection tool, fill, stroke, shadow and glow only show once there is a selection or a
  selection layer is picked, since they have nothing to work on before that.

- The size of shadow and glow can now be set to one decimal place, and a new glow starts at size 5
  instead of 10.

- DNG files that store their pictures as JPEG XL now show their large embedded preview instead of
  a tiny thumbnail. Developing their raw data is not possible yet.

- Double-clicking a slider now returns it to its starting value everywhere. Lens correction,
  color grading blend, depth blur, the mask and selection range sliders and two dialog sliders used
  to jump to zero instead. Depth blur now starts at zero strength, the same value its reset uses.

- Raw files from the Fujifilm X100VI get better matched brightness and colors.

- Save as, export, apply adjustments and the other dialogs with a target folder remember whether
  you last picked the current or the last folder.
