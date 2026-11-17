// Reference oracle: mechanically translated from Spoilerhead ART CTL V0.02 (C) 2026.
// Original plugins: https://github.com/spoilerhead/asp_plugins
using System;
static class SiliconBonkCtl {
// @ART-label: "SiliconBonk"
// @ART-param: ["hue", "Hue", 0.0, 360.0, 0.0, 1.0]
// @ART-param: ["luma", "Luminance change", -100.0, 100.0, 0.0, 1.0]
// @ART-param: ["saturation", "Saturation", 0.0, 200.0, 0.0, 1.0]
// @ART-param: ["mids", "Midpoint", -100.0, 100.0, 0.0, 1.0]
// @ART-param: ["contrast", "Contrast", -100.0, 1000.0, 0.0, 1.0]
// ART-param: ["gamma", "Gamma", 1.0, 5.0, 2.2, 0.01]
// @ART-param: ["preserve_details", "Preserve highlights/details", false]

// Port to ART (C) Spoilerhead 2026
// V0.02

private static double sb_clip(double x, double lo, double hi)
{
    if (x < lo) return lo;
    if (x > hi) return hi;
    return x;
}

private static double sb_min3(double x, double y, double z)
{
    double m = x;
    if (y < m) m = y;
    if (z < m) m = z;
    return m;
}

private static double sb_max3(double x, double y, double z)
{
    double m = x;
    if (y > m) m = y;
    if (z > m) m = z;
    return m;
}

private static double sb_fastsin(double x)
{
    double y = x;
    double pi = 3.14159265358979323846;
    if (y < -pi) y = y + 2.0 * pi;
    else if (y > pi) y = y - 2.0 * pi;
    if (y < 0.0) return (1.27323954 + 0.405284735 * y) * y;
    return (1.27323954 - 0.405284735 * y) * y;
}

private static double sb_fastcos(double x)
{
    return sb_fastsin(x + 3.14159265358979323846 / 2.0);
}

public static void ART_main(double r, double g, double b,
              out double r_out, out double g_out, out double b_out,
              double hue, double luma, double saturation, double mids, double contrast,
             bool preserve_details)
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

    double rr = Math.Pow(sb_clip(r, 0.0, 1.0), inv_gamma);
    double gg = Math.Pow(sb_clip(g, 0.0, 1.0), inv_gamma);
    double bb = Math.Pow(sb_clip(b, 0.0, 1.0), inv_gamma);

    double mn = sb_min3(rr, gg, bb);
    double mx = sb_max3(rr, gg, bb);
    double val = 0.299 * rr + 0.587 * gg + 0.114 * bb;
    double sat = 0.0;
    double h = 0.0;

    if (val > 0.000001 && mx - mn > 0.000001) {
        sat = (mx - mn) / val;

        double nr = rr / mx;
        double ng = gg / mx;
        double nb = bb / mx;
        double nmn = sb_min3(nr, ng, nb);
        double nmx = sb_max3(nr, ng, nb);
        double d = nmx - nmn;
        nr = (nr - nmn) / d;
        ng = (ng - nmn) / d;
        nb = (nb - nmn) / d;
        nmx = sb_max3(nr, ng, nb);

        if (nmx == nr) {
            h = (ng - nb) / 6.0;
            if (h < 0.0) h = h + 1.0;
        } else if (nmx == ng) {
            h = 1.0 / 3.0 + (nb - nr) / 6.0;
        } else {
            h = 2.0 / 3.0 + (nr - ng) / 6.0;
        }
    }

    double opt_h = hue / 360.0;
    double alpha = 0.5 * sb_fastcos(
        (h - opt_h) * 2.0 * 3.14159265358979323846
    );

    alpha = alpha * sb_clip(sat * (2.0 - sat), 0.0, 1.0);

    double lmod = 1.0 + 0.5 * (luma / 50.0);
    double val_mod = lmod * val;
    double val_new = val_mod * alpha + val * (1.0 - alpha);

    if (preserve_details) {
        val_new =
            ((-0.4481 * val_new + 0.5489) * val_new + 0.7659)
            * val_new + 0.03891;
    }

    val_new = val_new
            + (mids / 100.0)
            * (val_new - val_new * val_new);

    double c = contrast / 100.0;
    double linear_c = 1.0;

    if (c > 1.0) {
        linear_c = 1.0 + 2.0 * (c - 1.0) * (c - 1.0);
        c = 1.0;
    }

    val_new = sb_clip(val_new, 0.0, 1.0);

    val_new = val_new
            - (c / 4.4)
            * sb_fastsin(
                2.0 * 3.14159265358979323846 * val_new
              );

    val_new = (val_new - 0.5) * linear_c + 0.5;

    val = sb_clip(val_new, 0.0, 1.0);
    sat = sat * (saturation / 100.0);

    if (val <= 0.000001) {
        rr = 0.0;
        gg = 0.0;
        bb = 0.0;
    } else if (sat <= 0.000001) {
        rr = val;
        gg = val;
        bb = val;
    } else {
        double hf = h * 6.0;
        double f;
        double cc = val * sat;
        double qv;
        double tv;

        if (hf < 1.0) {
            f = hf;
            qv = cc * (1.0 - f);
            tv = cc * f;

            rr = cc;
            gg = tv;
            bb = 0.0;
        } else if (hf < 2.0) {
            f = hf - 1.0;
            qv = cc * (1.0 - f);
            tv = cc * f;

            rr = qv;
            gg = cc;
            bb = 0.0;
        } else if (hf < 3.0) {
            f = hf - 2.0;
            qv = cc * (1.0 - f);
            tv = cc * f;

            rr = 0.0;
            gg = cc;
            bb = tv;
        } else if (hf < 4.0) {
            f = hf - 3.0;
            qv = cc * (1.0 - f);
            tv = cc * f;

            rr = 0.0;
            gg = qv;
            bb = cc;
        } else if (hf < 5.0) {
            f = hf - 4.0;
            qv = cc * (1.0 - f);
            tv = cc * f;

            rr = tv;
            gg = 0.0;
            bb = cc;
        } else {
            f = hf - 5.0;
            qv = cc * (1.0 - f);
            tv = cc * f;

            rr = cc;
            gg = 0.0;
            bb = qv;
        }

        double m =
            val
            - (0.299 * rr
             + 0.587 * gg
             + 0.114 * bb);

        rr = rr + m;
        gg = gg + m;
        bb = bb + m;
    }

    rr = sb_clip(rr, 0.0, 1.0);
    gg = sb_clip(gg, 0.0, 1.0);
    bb = sb_clip(bb, 0.0, 1.0);

    /*
     * Undo the gamma transform before returning to ART.
     */
    r_out = Math.Pow(rr, gamma);
    g_out = Math.Pow(gg, gamma);
    b_out = Math.Pow(bb, gamma);
}
}
