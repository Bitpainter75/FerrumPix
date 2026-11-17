// Reference oracle: mechanically translated from Spoilerhead ART CTL V0.02 (C) 2026.
// Original plugins: https://github.com/spoilerhead/asp_plugins
using System;
static class FatToniCtl {
// @ART-label: "FatToni"
// @ART-param: ["highlight_hue", "Highlight hue", 0.0, 360.0, 0.0, 1.0]
// @ART-param: ["highlight_sat", "Highlight saturation", 0.0, 100.0, 0.0, 1.0]
// @ART-param: ["shadow_hue", "Shadow hue", 0.0, 360.0, 0.0, 1.0]
// @ART-param: ["shadow_sat", "Shadow saturation", 0.0, 100.0, 0.0, 1.0]
// @ART-param: ["separation", "Separation (mix)", 0.0, 100.0, 0.0, 1.0]
// @ART-param: ["mids", "Midpoint", -100.0, 100.0, 0.0, 1.0]
// @ART-param: ["contrast", "Contrast", -100.0, 1000.0, 85.0, 1.0]
// @ART-param: ["base_hue", "Base hue", 0.0, 360.0, 0.0, 1.0]
// @ART-param: ["base_sat", "Base saturation", 0.0, 100.0, 0.0, 1.0]
// @ART-param: ["base_luma", "Base luminance", 0.0, 100.0, 100.0, 1.0]


// Port to ART (C) Spoilerhead 2026
// V0.02



private static double ft_clip(double x, double lo, double hi)
{
    if (x < lo) return lo;
    if (x > hi) return hi;
    return x;
}

private static double ft_fastsin(double x)
{
    double y = x;
    double pi = 3.14159265358979323846;
    if (y < -pi) y = y + 2.0 * pi;
    else if (y > pi) y = y - 2.0 * pi;
    if (y < 0.0) return (1.27323954 + 0.405284735 * y) * y;
    return (1.27323954 - 0.405284735 * y) * y;
}

private static double ft_hue2rgb(double p, double q, double t)
{
    double tt = t;
    if (tt < 0.0) tt = tt + 1.0;
    if (tt > 1.0) tt = tt - 1.0;
    if (tt < 1.0 / 6.0) return p + (q - p) * 6.0 * tt;
    if (tt < 1.0 / 2.0) return q;
    if (tt < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - tt) * 6.0;
    return p;
}

private static double ft_hsl_channel(double h, double s, double l, double offset)
{
    double q;
    double p;
    if (l <= 0.000001) return 0.0;
    if (s <= 0.000001) return l;
    if (l <= 0.5) q = l * (1.0 + s);
    else q = l + s - l * s;
    p = 2.0 * l - q;
    return ft_hue2rgb(p, q, h + offset);
}

private static double ft_screen(double f, double b)
{
    return 1.0 - (1.0 - f) * (1.0 - b);
}

private static double ft_overlay(double f, double b)
{
    return b * ft_screen(f, b) + (1.0 - b) * (f * b);
}

private static double ft_blend(double f, double b, double o)
{
    return f * o + b * (1.0 - o);
}

private static double ft_softmix(double f, double b)
{
    return b + f - 0.5;
}

public static void ART_main(double r, double g, double b,
              out double r_out, out double g_out, out double b_out,
              double highlight_hue, double highlight_sat,
              double shadow_hue, double shadow_sat,
              double separation, double mids, double contrast,
              double base_hue, double base_sat, double base_luma)
{
    /*
     * Encode into the gamma-adjusted working domain.
     *
     * gamma = 1.0 -> no gamma transformation
     * gamma = 2.2 -> x^(1/2.2)
     * gamma = 5.0 -> x^(1/5.0)
     */
	double gamma = 2.2;
    double inv_gamma = 1.0 / gamma;

    double rr = Math.Pow(ft_clip(r, 0.0, 1.0), inv_gamma);
    double gg = Math.Pow(ft_clip(g, 0.0, 1.0), inv_gamma);
    double bb = Math.Pow(ft_clip(b, 0.0, 1.0), inv_gamma);

    double hh = highlight_hue / 360.0;
    double hs = highlight_sat / 100.0;
    double sh = shadow_hue / 360.0;
    double ss = shadow_sat / 100.0;
    double bh = base_hue / 360.0;
    double bs = base_sat / 100.0;
    double bl = base_luma / 100.0;

    double hr = ft_hsl_channel(hh, hs, 0.5, 1.0 / 3.0);
    double hg = ft_hsl_channel(hh, hs, 0.5, 0.0);
    double hb = ft_hsl_channel(hh, hs, 0.5, -1.0 / 3.0);
    double sr = ft_hsl_channel(sh, ss, 0.5, 1.0 / 3.0);
    double sg = ft_hsl_channel(sh, ss, 0.5, 0.0);
    double sb = ft_hsl_channel(sh, ss, 0.5, -1.0 / 3.0);
    double br = ft_hsl_channel(bh, bs, bl, 1.0 / 3.0);
    double bg = ft_hsl_channel(bh, bs, bl, 0.0);
    double bz = ft_hsl_channel(bh, bs, bl, -1.0 / 3.0);

    double luma = (rr + gg + bb) / 3.0;
    double a = ft_clip(luma + mids / 200.0, 0.0, 1.0);
    a = ft_clip(a - (0.5 * separation / 100.0) * ft_fastsin(2.0 * 3.14159265358979323846 * a), 0.0, 1.0);

    double tr = ft_blend(hr, sr, a);
    double tg = ft_blend(hg, sg, a);
    double tb = ft_blend(hb, sb, a);
    double c = contrast / 100.0;

    rr = ft_blend(ft_overlay(tr, rr), ft_clip(ft_softmix(tr, rr), 0.0, 1.0), c);
    gg = ft_blend(ft_overlay(tg, gg), ft_clip(ft_softmix(tg, gg), 0.0, 1.0), c);
    bb = ft_blend(ft_overlay(tb, bb), ft_clip(ft_softmix(tb, bb), 0.0, 1.0), c);

    rr = ft_clip(br * rr, 0.0, 1.0);
    gg = ft_clip(bg * gg, 0.0, 1.0);
    bb = ft_clip(bz * bb, 0.0, 1.0);

    /*
     * Undo the gamma transform before returning to ART.
     */
    r_out = Math.Pow(rr, gamma);
    g_out = Math.Pow(gg, gamma);
    b_out = Math.Pow(bb, gamma);
}

}
