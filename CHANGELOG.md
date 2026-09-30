## 0.9.57

### What's new

- A compact adjustments mode in the editor settings puts Adjust, Color, Details, Effects and
  Filter into one tool. It shows all groups one below the other, and by default only one of them
  is open at a time: opening a group closes the previous one. A second setting turns that off; the
  tool then keeps open what you opened and remembers it.

### Fixes

- Collapsed groups in the adjustment panel are lower. An empty strip under each heading made them
  take more room than needed.

- Copying a selection in the editor no longer freezes the window. With a large raw file,
  CTRL+C, CTRL+X and the Copy button used to hold everything for several seconds; the copy is now
  prepared in the background, and pasting it as a new layer is quicker too.

- Pasting a picture as a new layer now ends the selection it came from. It used to stay, and
  switching to an adjustment tool afterwards turned it into an extra adjustment layer.

- The map loads every tile it still needs. When one view stopped waiting for a tile, other places
  showing the same tile could be left without it.

