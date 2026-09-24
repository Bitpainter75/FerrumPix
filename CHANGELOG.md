## FerrumPix 0.9.51

### What's new

- Text layers support left, center, right, and justified alignment for multiline text.
- Text-shadow offsets support 0.1-step fine adjustment; hold Shift in the value field for whole-number steps.
- New text, brush strokes, and shape or symbol outlines automatically contrast with the document background.

### Fixes

- Text selection frames now follow the visible glyphs and remain synchronized after resizing and undoing.
- Undo and redo now refresh a selected text frame after any text or font change.
- Moving a text layer now redraws the complete scaled shadow, including areas outside its selection frame.
- Internal gallery moves now use pointer gestures instead of native X11 drag-and-drop, keeping the normal cursor while showing valid folder and tree targets.
- Saving an edited FPX no longer fails when Avalonia cannot scale the editor's preview bitmap.
- Saving an FPX in place no longer leaves it marked as unsaved, avoiding a redundant prompt when returning home.
