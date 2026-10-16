## 0.9.66

### What's new

- **RAW development on macOS without Homebrew.** The Mac packages now bring their own LibRaw, so
  RAW files are developed from the sensor data even if nothing is installed. A LibRaw from
  Homebrew is still preferred when it is there.

- **Compare a single curve.** Right-click RGB, R, G, B or L above the tone curve to set that
  channel's curve back to a straight line for a moment; the next right-click brings it back,
  just like comparing a single slider.

- **Compare a whole group.** The eye next to the reset button of Light, Color, Tone curve,
  Color mixer, Colour grading, Calibration, Channel Mixer, Gradient Map, Posterize, Threshold,
  Sharpness, Blur, Noise, Details, Vignette and Grain hides that group's adjustments for a
  moment, so you see the photo without them; click it again to bring them back.

### Fixes

- Enlarging the canvas no longer makes the photo shrink inside the editor. Even one extra pixel
  could show the picture at half its size with a huge border around it; the saved image was
  always right, now the editor shows the same.

- Scans saved as TIFF or DNG are read far less often: reading their details no longer loads the
  whole file, so indexing watched folders, opening a picture in the editor and the info panel are
  much quicker on a network drive. A film scan as DNG used to travel over the network up to four
  times, now about once.

- The Flatpak and the Microsoft Store version no longer look for a newer version on GitHub;
  updates come through the software manager or the Store that installed them.

- In the Flatpak, pictures in a folder you shared through the system's folder dialog can be
  renamed, moved and deleted again, like on a second drive.

- In the Flatpak, the window buttons follow your desktop's order again instead of standing the
  wrong way round, and the folder tree shows your drives instead of the inside of the sandbox.

