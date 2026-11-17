# Writing extensions for FerrumPix

An extension adds a tool with its own sliders to the FerrumPix editor. It is a single JSON file:
it names the sliders and contains a small function that turns one color into another. You do
not need to build FerrumPix or know VB.NET to write one.

Once at least one extension is loaded, the editor shows the **Extensions** tool in the
Adjustments group. Every loaded extension appears there as its own group with an on switch, its
sliders and a reset button. Without any extension the tool does not appear at all.

## Installing an extension

Open **Settings**, section **Extensions**, and choose **Load extension**. FerrumPix checks the
file completely before it copies it into its extension folder. If something is wrong, the file is
not loaded and the section shows the reason, including the line number of a shader error. The
same section lists every loaded extension and removes the ones you no longer want.

**Reload** fetches every extension again from the file it was loaded from, so while you work on
an extension you only edit your own file and choose Reload. If that file is no longer there,
the copy in the extension folder is used. If it is there but cannot be loaded, the last good
copy stays and the reason is shown.

The list below sets the order of the groups in the editor with the arrows, and the eye hides an
extension in the editor. Neither changes a picture: a hidden extension keeps working in the
pictures that use it, it only does not show its sliders.

FerrumPix comes with the four examples of this guide. They are hidden at first, so the editor
only shows the Extensions tool to those who want it: click the eye next to one to bring it into
the editor. They cannot be removed. If you load your own file with the id of one of them, yours
is used instead, so you can try a newer version without waiting for a FerrumPix release.

In the editor, each group has the same buttons as the built in ones: the eye compares the
picture without the extension, the arrow resets the whole group. A slider goes back to its
default with a double click and compares with it on a right click; color fields and curves have
a small reset button of their own. Every change is a step in the history, with Undo and Redo.

The extension folder is shown at the bottom of the section. You can also copy files there by
hand and choose **Reload**:

| System | Folder |
|---|---|
| Linux and macOS | `~/.config/FerrumPix/Extensions` |
| Windows | `%APPDATA%\FerrumPix\Extensions` |

## What an extension can and cannot do

An extension sees **one pixel at a time**: its color goes in, a new color comes out, and if it
asks for it, also where the pixel lies in the picture. That is enough for black and white
conversions, toning, color looks, curves, vignettes, gradients and everything else that works
pixel by pixel. It is not enough for anything that needs the neighbors of a pixel, such as
blurring, sharpening or grain.

The function runs as a shader, on the graphics card when FerrumPix uses it and otherwise on the
processor. A shader cannot open files, reach the network or read anything except the pixel it is
given. An extension is therefore safe to try: the worst it can do is give your picture an odd
color.

An extension works where the other adjustments work: on the whole picture, on a mask or
selection layer, and on a single object. Select the layer or object, open the Extensions tool,
and the sliders change that layer only, just like Light or Color would.

The settings of an extension are stored with the picture, in the same sidecar file as every
other edit. They are not written into XMP presets, because no other program would know what to
do with them.

## Examples

Four complete extensions come with this guide, in [`docs/extensions`](extensions), and with
FerrumPix itself, hidden until you show them in the settings:

- [`spoilerhead.silicon-bonk.json`](extensions/spoilerhead.silicon-bonk.json) turns a finished
  color picture into black and white. Hue and Luminance work like a color filter in front of the
  lens, with Midpoint, Contrast, Residual color and a switch that protects highlights and details.
- [`spoilerhead.fat-toni.json`](extensions/spoilerhead.fat-toni.json) tones highlights and
  shadows separately and can tint the base like a paper color. It runs after grain, so the grain
  is toned too.

- [`example.curve-toning.json`](extensions/example.curve-toning.json) shapes the brightness with
  a curve and tones shadows and highlights with two colors. It shows a curve, color fields and
  presets (Sepia, Selenium, Cold).
- [`example.light-falloff.json`](extensions/example.light-falloff.json) darkens or brightens the
  picture towards its edges, round or from one side. It shows a choice, help texts and the
  position in the picture.

The first two are ports of earlier work by Spoilerhead (Silicon Bonk based on an idea by Bernd
Gruber) and show sections, toggles, constants and helper functions. Load them to try them out,
or use them as a starting point.

## A first extension

Save this as `example.warm-tint.json` and load it:

```json
{
  "format": 1,
  "id": "example.warm-tint",
  "version": 1,
  "name": { "en": "Warm tint", "de": "Warmer Ton" },
  "description": "Moves the colors toward orange.",
  "author": "Your name",
  "license": "MIT",
  "controls": [
    { "key": "amount", "type": "slider", "min": 0, "max": 100, "default": 30,
      "label": { "en": "Amount", "de": "Stärke" } }
  ],
  "shader": [
    "float3 effect(float3 color) {",
    "    float3 warm = color * float3(1.08, 1.0, 0.88);",
    "    return mix(color, warm, amount / 100.0);",
    "}"
  ]
}
```

The editor now has a group **Warm tint** with one slider. Moving the slider switches the
extension on for this picture.

## The file

| Field | Required | Meaning |
|---|---|---|
| `format` | yes | The version of this file format. Always `1` for now. |
| `id` | yes | A unique name in lower case: letters, digits, dots and hyphens, at least two parts, at most 64 characters. Use your own name as the first part, for example `jane.film-look`. The id is what pictures store; never change it once you published the extension. |
| `version` | yes | A whole number from 1. Raise it whenever the calculation changes, see below. |
| `name` | yes | The name in the editor. Text, or one text per language. |
| `description` | no | One or two sentences shown in the editor and in the settings. |
| `type` | no | The kind of extension. Only `shader` for now, which is also the default. |
| `author`, `license` | no | Shown in the settings. Please always state a license. |
| `url` | no | Where a longer description lives, for example a README in your repository. The settings show it as a link. It must be a full web address starting with `https://`; anything else is refused when the file is loaded. |
| `stage` | no | Where in the processing the extension runs, see below. Default `before-grain`. |
| `priority` | no | The order within the stage, see below. A whole number, default 0. |
| `controls` | no | The sliders, switches, color fields and curves, at most 32. |
| `presets` | no | Named sets of values to choose from, at most 32, see below. |
| `shader` | yes | The calculation, as one text or as a list of lines. |

A file may be at most 256 KB. Comments (`//` and `/* */`) and trailing commas are allowed.

### Texts in several languages

Every text field (`name`, `description`, `label`, `section`, `hint`) is either plain text, which counts as
English, or an object with one text per language code:

```json
"name": { "en": "Split toning", "de": "Teiltonung", "fr": "Virage partiel" }
```

FerrumPix uses the language of its user interface, then English, then the first text it finds.
The codes are those of the FerrumPix languages: `en de nl sv da nb fi es fr it pt pl cs ru zh-CN
ja ko id tr th hi`.

### Controls

```json
{ "key": "contrast", "type": "slider", "min": -50, "max": 200, "default": 0, "decimals": 0,
  "label": "Contrast", "section": "Mixing" }
```

| Field | Meaning |
|---|---|
| `key` | The name under which the shader sees the value. Starts with a lower case letter, then letters and digits, at most 32 characters. Names of the shader language (`color`, `float3`, `if` and so on) and names starting with `fp` are reserved. |
| `type` | `slider` (default), `toggle`, `choice`, `color` or `curve`. |
| `min`, `max`, `default` | Range and starting value of a slider. A toggle takes `"default": true` or `false`, a color `"#RRGGBB"` (default `#808080`), a curve a list of points (default `[[0, 0], [1, 1]]`). |
| `decimals` | Decimal places of a slider: 0, 1 or 2. Default 0, whole numbers. The value is rounded to them, so `"decimals": 2` with a range from 0 to 1 gives steps of 0.01. |
| `track` | Optional color track of a slider, see below. |
| `label` | The text next to the control. |
| `section` | Optional heading shown above this control, to group the controls that follow. |
| `hint` | Optional help text, shown when the pointer rests on the label. One or two sentences on what the control does. |

### Choices

A **choice** shows a list to pick one of several options, for a film type, a shape or a mode:

```json
{ "key": "shape", "type": "choice", "default": 0, "label": "Shape",
  "options": [
    { "value": 0, "label": "Round" },
    { "value": 1, "label": { "en": "From the top", "de": "Von oben" } },
    { "value": 2, "label": "From the left" }
  ] }
```

Each option has a number and a label, 2 to 32 of them, no number twice. The shader gets the
number of the chosen option, and the picture stores that number, not the place in the list. You
may therefore reorder the options or add new ones later without old pictures choosing something
else. `default` is the number of an option; without it the first option is the default. Compare
with a margin in the shader, `if (shape < 0.5)` rather than `if (shape == 0.0)`.

### Color fields and curves

A **color** field shows a color button with the color picker of FerrumPix:

```json
{ "key": "shadowColor", "type": "color", "default": "#5A3A1E", "label": "Shadows" }
```

A **curve** shows a curve field like the tone curve, where points are added with a click, moved
by dragging and removed with a right click. Points are written as `[x, y]` from 0 to 1, x rising,
2 to 16 of them. FerrumPix draws the curve through them the same way as the tone curve: rising
points give a rising curve without overshooting.

```json
{ "key": "toneCurve", "type": "curve", "default": [[0, 0], [0.5, 0.55], [1, 1]],
  "label": "Brightness curve" }
```

Neither of them takes `min`, `max`, `decimals` or `track`.

### Presets

`presets` offers named sets of values as a choice at the top of the group. Choosing one sets
every control it names to its value and every other control to its default, and switches the
extension on, as one step that Undo takes back. Values are written like in the controls: numbers
for sliders, `true` or `false` for toggles, `"#RRGGBB"` for colors and lists of points for
curves.

```json
"presets": [
  { "name": { "en": "Sepia", "de": "Sepia" },
    "values": { "shadowColor": "#5A3A1E", "highlightColor": "#F2DFC0", "amount": 60,
                "toneCurve": [[0, 0.04], [0.5, 0.5], [1, 0.97]] } },
  { "name": "Cold",
    "values": { "shadowColor": "#24364F", "highlightColor": "#DCE6F0" } }
]
```

### Color tracks

With the setting **Colored sliders**, a slider can show what it does as a color track, like
the built in ones. `track` takes one of these forms:

| `track` | Track |
|---|---|
| `"hue"` | The whole color wheel, for a slider from 0 to 360. |
| `"black-white"` | Black to white, for midpoints and brightness. |
| `"temperature"` | Blue to yellow, like the white balance. |
| `"tint"` | Green to magenta, like the tint. |
| `["#3D7BD9", "#C8CCD0", "#E3C13B"]` | Your own colors from left to right, 2 to 8 of them. |
| `{ "saturation": "shadowHue" }` | Gray to the color of another slider of this extension, for a saturation. |
| `{ "luminance": "shadowHue" }` | Dark to light in the color of another slider, for a luminance. |

The last two follow the other slider while it moves: a saturation track shows the hue that is
set right now. The other slider must be a hue slider from 0 to 360 of the same extension.

```json
{ "key": "shadowHue", "type": "slider", "track": "hue", "min": 0, "max": 360, "default": 220,
  "label": "Hue", "section": "Shadows" },
{ "key": "shadowSaturation", "type": "slider", "track": { "saturation": "shadowHue" },
  "min": 0, "max": 100, "default": 0, "label": "Saturation" }
```

### In the shader

In the shader every slider, toggle and choice is a `float` with the value of the control as shown
in the editor: a slider from -50 to 200 gives you -50 to 200, a toggle gives 0 or 1, a choice the
number of the chosen option. Values outside the range never reach the shader; FerrumPix clamps
them.

A color field is a `float3` with red, green and blue from 0 to 1 (sRGB). A curve is a function
with the name of its key, `float toneCurve(float x)`, for x from 0 to 1. Inside it reads a table
of 256 values with 16 bit each and interpolates linearly between them.

### Where the extension runs

| `stage` | Position |
|---|---|
| `before-grain` | After all other adjustments, before grain. The picture gets its grain afterwards. Right for black and white conversions and color looks. |
| `after-grain` | After grain. The grain is changed along with the picture, for example toned. Right for toning. |

Within the same stage, extensions run by `priority`, the lower number first, and with the same
number by their `id`. The order depends only on the files, never on the picture or the order in
which extensions were switched on, so two pictures with the same settings always look the same.
Leave generous gaps, for example `-1000` for something that should run early and `1000` for
something that should run late, so that others can fit in between. The order of the groups in
the settings only affects the display, not the processing.

## The shader

The shader is written in SkSL, the shading language of the Skia graphics library. It is very
close to GLSL. It must define exactly this function:

```glsl
float3 effect(float3 color)
```

`color` is the pixel in sRGB, each channel from 0 to 1, without transparency (FerrumPix takes
care of transparent pixels). Return the new color in the same form. Results outside 0 to 1 are
clipped.

### The position in the picture

If the effect depends on where a pixel lies, as a vignette or a gradient does, define the
function with a second parameter instead:

```glsl
float3 effect(float3 color, float2 pos)
```

`pos` runs from 0 to 1 across the picture: `(0, 0)` is the top left corner, `(1, 1)` the bottom
right. It always refers to the whole finished picture, after cropping and rotating, the same for
the preview, the zoomed view and the export, so an effect looks the same in all of them. On an
object it refers to the object.

`fpSize` (a `float2`) gives the size of the picture in pixels, but only use it for the aspect
ratio, `fpSize.x / fpSize.y`, for example to make a vignette round instead of oval. The number of
pixels itself differs between preview and export; an effect that counts pixels would look
different in each.

You may define constants and further functions above `effect`. The usual functions are available:
`mix`, `clamp`, `pow`, `sin`, `dot`, `fract`, `floor`, `min`, `max`, `abs`, `step`,
`smoothstep`, `length` and the others of GLSL. The examples write vectors as `float3`.

FerrumPix adds these helper functions:

| Function | Meaning |
|---|---|
| `float fpLuma(float3 c)` | Luminance with the Rec. 709 weights. |
| `float3 fpToLinear(float3 c)` | sRGB to linear light. |
| `float3 fpToSrgb(float3 c)` | Linear light to sRGB. |
| `float3 fpRgbToHsl(float3 c)` | RGB to hue, saturation, lightness, each 0 to 1. |
| `float3 fpHslToRgb(float3 hsl)` | Back from HSL to RGB. |

If the shader does not compile, the settings show the message of the compiler with the line
number counted in your shader, so `line 2` is the second line of your `shader` text.

### Things to keep in mind

- **Neutral by default.** Whenever possible, the default values should leave the picture as it
  is. Then switching an extension on does not surprise anyone, and resetting means "as before".
- **Every pixel independently.** The function must give the same result for the same color and
  position, whatever else is in the picture. FerrumPix may calculate the picture in bands or tiles, on the
  graphics card or on the processor; with a pure function all of them agree.
- **Precision.** Calculate in `float`. FerrumPix gives you full precision for 16 bit exports.
- **Speed.** The function runs for every pixel of every preview. A few dozen operations are
  fine; long loops are not.

## Versions

Pictures store the id, the version and the slider values of every extension used on them. When
you change the calculation so that the same values give a different picture, raise `version`.
FerrumPix then tells the user of a picture made with an older version that the extension has
changed. It still uses the version that is installed; there is only ever one.

Adding a control is fine without breaking anything: pictures that do not know it use its
default. Never rename or remove the key of a control that existing pictures use, and never
change the meaning of a value. If you must, publish a new extension with a new id.

When a picture uses an extension that is not installed, FerrumPix keeps its settings, shows the
user which extension is missing and leaves the picture otherwise as it is. Once the extension is
installed again, it takes effect again.

## Testing

1. Load the file in the settings. If it is rejected, the reason is shown there.
2. Open a picture with strong colors and a range from black to white, switch the extension on and
   move every slider over its full range.
3. Check the default values: does the picture stay unchanged, or change exactly as intended?
4. Export once as a 16 bit TIFF to see the extension at full precision.

## Sharing

An extension is one file, so sharing it is easy: publish it wherever you like. Please put the
license in the file and in your repository. If you would like your extension to ship with
FerrumPix, open an issue with a link to it.
