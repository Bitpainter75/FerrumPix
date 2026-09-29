## Unreleased

### What's new

- **Remove color fringes by hand.** The lens correction has two new sliders, Red/Cyan and
  Blue/Yellow, for fringes the lens profile leaves behind or for lenses without a profile.

- **Aspect ratios stay put while cropping.** Choosing 16:9, Original or another ratio keeps the
  crop frame in that shape while you drag it, typing a width or height adjusts the other, and
  Free lets it go again. The edge and size values now follow the frame while you drag.

### Fixes

- Lens correction: removing color fringes at the image corners made them stronger instead of
  weaker. It now works in the right direction and takes most of the fringe away.

- Raw files develop noticeably faster, and the lens correction sliders no longer redevelop the
  picture at every step while you drag them.

- Canon CR3 pictures taken in portrait orientation are shown upright in the viewer and on gallery
  tiles again instead of lying on their side.

- At 100 percent, moving a lens correction slider no longer jumps back to the middle of the
  picture, so you can watch the effect where you zoomed in.

- While Move is switched on in the editor, the pointer shows a hand, so it is clear why the tools
  do not respond to clicks.

- A saved edit that was applied but not saved is offered again the next time the picture is
  opened, instead of the picture silently showing up without it. While such an edit is applied,
  the lens correction explains why it is not available instead of simply disappearing.

