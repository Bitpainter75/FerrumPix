# Bundled libmpv runtime

FerrumPix bundles the `video-default` playback runtime from
[`media-kit/libmpv-darwin-build`](https://github.com/media-kit/libmpv-darwin-build):

- Release: `v0.7.2`
- Asset: `libmpv-libs_v0.7.2_macos-universal-video-encodersgpl.tar.gz`
- SHA-256: `34354f72decd42b291097306dddcd1748ad51fcb84797347159af50b3ee9d9af`
- Architectures: macOS arm64 and x86_64
- Variant: video playback plus image encoders required by the thumbnail screenshot pipeline

The runtime contains libmpv, FFmpeg, libass, dav1d, FreeType, FriBidi,
HarfBuzz, mbedTLS, libpng, uchardet, libxml2, and GPL-compatible codec
components. The corresponding build recipes and exact upstream source
revisions are available from the tagged source repository above. The
libraries are dynamically loaded and remain replaceable by compatible builds.
