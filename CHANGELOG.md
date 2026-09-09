## FerrumPix 0.9.42 Unreleased

### What's new

### Fixes

- A video from an Immich or Nextcloud server plays instead of taking the app down with it. Such a
  video only becomes a file once it has been downloaded, and the viewer started it a second time on
  the way there; on macOS that ended the program on the spot. Videos on your own disk were never
  affected, and repeating a video still works.

- Denoising and removing an object now fit themselves to the graphics card. Both used to ask the
  card for more memory than a small one has, and on such a card the app did not report a problem,
  it closed. FerrumPix now asks the card how much memory it has and computes in smaller pieces when
  there is not much, which costs a little detail on those cards and nothing at all on the others.

