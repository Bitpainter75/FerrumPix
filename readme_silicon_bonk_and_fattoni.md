# Silicon Bonk and Fat Toni

The main goal is simple: **make a fast, striking black-and-white version of an already corrected color image**. Finish the color photograph first, including its curves and color adjustments, then enable Silicon Bonk and shape the monochrome result with a few controls. Fat Toni adds optional toning afterward.

The tools take inspiration from what photographic filters, toning and paper color do to an image. They were designed for direct, responsive digital editing, rather than to reproduce traditional darkroom processes or simulate their chemistry.

## How they came to life

Silicon Bonk began with an idea and a Mathbox test preset by **Bernd Gruber**, an Austrian professional photographer known as **grubernd**. **Spoilerhead** turned that concept into a Bibble 5 plugin. Its deliberately late position in the processing pipeline let photographers convert a finished color edit without undoing the work that made it look good. Community feedback brought the residual-color control and a reset button.

Fat Toni emerged while Spoilerhead was experimenting with toning during Silicon Bonk's development. It became a separate plugin to keep Silicon Bonk's interface focused. It tones highlights and shadows and can also tint the base—the equivalent of the photographic “paper.” It works with monochrome conversions or directly on color photographs. Both tools later became available for AfterShot Pro; the original plugin source and a standalone command-line version of Silicon Bonk are linked below.

## Silicon Bonk — Adaptive Black&White

**Hue and Luminance work like the color filter you might put in front of a camera lens for black-and-white photography.** Hue selects the filter's color response; Luminance controls its strength and direction. Increasing Luminance lightens colors near the selected hue while darkening complementary colors. Negative Luminance reverses that relationship. Hue does not rotate or replace the photograph's colors.

For example, **Hue around 40 with increased Luminance gives a yellow-filter-like effect**. Use it to change how the original colors separate into light and dark tones. The selected and complementary colors are linked: making reds lighter and making cyans darker are two ways to approach the same tonal separation.

- **Midpoint** lightens or darkens the midtones.
- **Contrast** is a global contrast control with a sinusoidal response rather than a simple linear adjustment. Its curved response gives a useful approximation of film contrast and photographic paper gradation.
- **Residual Color** controls how much original color remains. Zero gives black and white; increasing it introduces color into the result.
- **Preserve highlights/details** gently protects both highlights and shadows, helping keep bright and dark detail from clipping too quickly as contrast increases.

## Fat Toni — Split Toning

Fat Toni applies color to the image's **highlights and shadows**, with optional coloring of the **base (“paper”)**. It does not perform the black-and-white conversion itself.

- **Highlights / Shadows:** choose each tone's Hue and Saturation. Zero saturation leaves that tone neutral.
- **Base:** choose the paper-like base color with Hue, Saturation and Luminance.
- **Mixing → Midpoint:** shift the balance between shadow and highlight tones.
- **Mixing → Separation:** control how distinctly the two tones separate across the brightness range.
- **Mixing → Contrast:** change the tonal character of the color blending.

Try warm highlights with cool shadows, or keep both subtle for a restrained print-like finish. Fat Toni can also give an already finished—or “cooked”—color image a final color cast, without needing a black-and-white conversion first.

## Using them in FerrumPix

The native FerrumPix tools adapt Spoilerhead's ART CTL ports. They process in this order:

**Vignette → Silicon Bonk → Fat Toni → Grain**

Start with a finished color edit. Enable Silicon Bonk with Residual Color at zero, adjust Hue/Luminance, then refine Midpoint and Contrast. Enable Fat Toni if you want toning. Finally, try a little Residual Color for a richer, partially colored result. Early users developed this workflow by refining the monochrome conversion and toning together, then letting a little original color shine through.

## References

- [Bernd Gruber / grubernd — photographer's information page](https://grubernd.at/info/)
- [Original Bibble / AfterShot Pro plugin source code](https://github.com/spoilerhead/asp_plugins)
- [Silicon Bonk standalone command-line version](https://github.com/spoilerhead/SiliconBonk_Standalone)
- [Silicon Bonk — AfterShot Pro description](https://www.aftershotpro.com/en/plugins/siliconbonk/)
- [Fat Toni — AfterShot Pro description](https://www.aftershotpro.com/en/plugins/fattoni/)
- [Original Silicon Bonk forum thread, archived](https://web.archive.org/web/20111005043718/https://support.bibblelabs.com/forums/viewtopic.php?f=98&t=19140)
- [Silicon Bonk discussion of Hue/Luminance, archived](https://web.archive.org/web/20111007231616/http://support.bibblelabs.com/forums/viewtopic.php?f=98&t=19140&sid=bf732e1efe51e8486a3bba7ba879e5ea&start=15)
- [Original Fat Toni forum thread, archived](https://web.archive.org/web/20111007221829/http://support.bibblelabs.com/forums/viewtopic.php?f=98&t=19230&sid=bf732e1efe51e8486a3bba7ba879e5ea)
