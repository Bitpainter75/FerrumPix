## Unreleased

### What's new

- **Opacity for the mask brush.** A stroke no longer has to cover fully: set the brush to 40 and it
  paints the mask to 40 percent, and subtracting takes away just as little. The setting is the
  coverage you get, so going over the same spot again does not deepen it - a stronger brush does.

- **Window buttons the way your desktop has them.** Close, maximize and minimize now take their
  side and their order from your desktop, so they sit where you are used to them, on the left as
  well. They stay one group, and buttons your desktop leaves out are still there - they are the
  only way to minimize from the title bar. The settings let you pin them to one side.

### Fixes

- Switching between gallery, viewer and editor is now immediate. Every switch used to rebuild the
  whole view, which took about a third of a second on the way to the gallery and well over half a
  second on the way to the editor. The views are kept now, so the switch takes a few milliseconds.
  The editor is still only built the first time you go there.

