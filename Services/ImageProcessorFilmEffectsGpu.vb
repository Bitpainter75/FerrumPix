Imports System
Imports SkiaSharp

Namespace Services
    Partial Public Class ImageProcessor

        ' Same CTL-derived math as ImageProcessorFilmEffects.vb. These are separate stages:
        ' grain must remain between monochrome conversion and toning. Keeping them in the
        ' detail session avoids a download/upload at each film effect. Deep exports still use
        ' the CPU companion through RunDetailStage; this GPU session stores 8-bit pixels.
        Private Shared Function SiliconBonkOnGpu(adj As ImageAdjustments) As Func(Of GRContext, SKImage, SKSurface)
            Dim hue = adj.SiliconBonkHue / 360.0F
            Dim luma = adj.SiliconBonkLuma / 100.0F
            Dim saturation = adj.SiliconBonkSaturation / 100.0F
            Dim mids = adj.SiliconBonkMidpoint / 100.0F
            Dim contrast = adj.SiliconBonkContrast / 100.0F
            Dim linearContrast = If(contrast > 1.0F, 1.0F + 2.0F * (contrast - 1.0F) * (contrast - 1.0F), 1.0F)
            contrast = Math.Min(contrast, 1.0F)
            Dim preserve = If(adj.SiliconBonkPreserveDetails, 1.0F, 0.0F)
            Return Function(gr, image)
                       Dim effect = StageEffect("SiliconBonk", FilmShaderCommon & SiliconBonkShader)
                       Dim u = New SKRuntimeEffectUniforms(effect)
                       u("hue") = hue : u("luma") = luma : u("saturation") = saturation
                       u("mids") = mids : u("contrast") = contrast
                       u("linearContrast") = linearContrast : u("preserve") = preserve
                       Using dither = BuildDitherImage()
                           Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image), ("filmDither", dither))
                       End Using
                   End Function
        End Function

        Private Shared Function FatToniOnGpu(adj As ImageAdjustments) As Func(Of GRContext, SKImage, SKSurface)
            ' The tone colors are constants for the entire image; use the CPU's HSL definition
            ' once so shader and CPU agree on hue wrapping and neutral base colors.
            Dim high = Array.ConvertAll(FilmHsl(adj.FatToniHighlightHue, adj.FatToniHighlightSaturation, 50.0), Function(v) CSng(v))
            Dim low = Array.ConvertAll(FilmHsl(adj.FatToniShadowHue, adj.FatToniShadowSaturation, 50.0), Function(v) CSng(v))
            Dim basis = Array.ConvertAll(FilmHsl(adj.FatToniBaseHue, adj.FatToniBaseSaturation, adj.FatToniBaseLuminance), Function(v) CSng(v))
            Dim mids = adj.FatToniMidpoint / 200.0F
            Dim separation = adj.FatToniSeparation / 200.0F
            Dim contrast = adj.FatToniContrast / 100.0F
            Return Function(gr, image)
                       Dim effect = StageEffect("FatToni", FilmShaderCommon & FatToniShader)
                       Dim u = New SKRuntimeEffectUniforms(effect)
                       u("highTone") = high : u("lowTone") = low : u("baseTone") = basis
                       u("mids") = mids : u("separation") = separation : u("contrast") = contrast
                       Using dither = BuildDitherImage()
                           Return DrawStage(gr, image.Width, image.Height, effect, u, ("src", image), ("filmDither", dither))
                       End Using
                   End Function
        End Function

        Private Const FilmShaderCommon As String = "
uniform shader src;
uniform shader filmDither;
const float filmPi = 3.14159265358979323846;
float filmSin(float x) {
    if (x < -filmPi) x += 2.0 * filmPi;
    else if (x > filmPi) x -= 2.0 * filmPi;
    return x < 0.0 ? (1.27323954 + 0.405284735 * x) * x
                   : (1.27323954 - 0.405284735 * x) * x;
}
float filmDomain(float v) {
    v = clamp(v, 0.0, 1.0);
    float linear = v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4);
    return pow(linear, 1.0 / 2.2);
}
float filmOutput(float v) {
    float linear = pow(clamp(v, 0.0, 1.0), 2.2);
    return linear <= 0.0031308 ? 12.92 * linear : 1.055 * pow(linear, 1.0 / 2.4) - 0.055;
}
float4 filmRead(float2 p) {
    float4 bytes = toBytes(src.eval(p));
    if (bytes.a <= 0.0) return float4(0.0);
    // Unlike unpremulBytes, the film CPU path retains the fractional quotient.
    return float4(bytes.rgb / bytes.a, bytes.a);
}
half4 filmWrite(float3 c, float a, float2 p) {
    float dither = float(filmDither.eval(mod(floor(p), float2(8.0)) + 0.5).r);
    float3 bytes = clamp(floor(c * 255.0 + 0.5 + dither), 0.0, 255.0);
    // Match PremultiplyByte/SkMulDiv255Round AFTER quantization, not writePremul's floor.
    float3 product = bytes * a + 128.0;
    bytes = floor((product + floor(product / 256.0)) / 256.0);
    return half4(half3(bytes / 255.0), half(a / 255.0));
}
"

        Private Const SiliconBonkShader As String = "
uniform float hue;
uniform float luma;
uniform float saturation;
uniform float mids;
uniform float contrast;
uniform float linearContrast;
uniform float preserve;
half4 main(float2 p) {
    float4 pixel = filmRead(p);
    if (pixel.a <= 0.0) return half4(0.0);
    float3 c = float3(filmDomain(pixel.r), filmDomain(pixel.g), filmDomain(pixel.b));
    float lo = min(c.r, min(c.g, c.b)), hi = max(c.r, max(c.g, c.b));
    float value = dot(c, float3(0.299, 0.587, 0.114));
    float sat = 0.0, h = 0.0;
    if (value > 0.000001 && hi - lo > 0.000001) {
        sat = (hi - lo) / value;
        if (hi == c.r) {
            h = (c.g - c.b) / (hi - lo) / 6.0;
            if (h < 0.0) h += 1.0;
        } else if (hi == c.g) h = 1.0 / 3.0 + (c.b - c.r) / (hi - lo) / 6.0;
        else h = 2.0 / 3.0 + (c.r - c.g) / (hi - lo) / 6.0;
    }
    float alpha = 0.5 * filmSin((h - hue) * 2.0 * filmPi + filmPi / 2.0);
    alpha *= clamp(sat * (2.0 - sat), 0.0, 1.0);
    value *= 1.0 + luma * alpha;
    if (preserve > 0.5) value = ((-0.4481 * value + 0.5489) * value + 0.7659) * value + 0.03891;
    value += mids * (value - value * value);
    value = clamp(value, 0.0, 1.0);
    value -= contrast / 4.4 * filmSin(2.0 * filmPi * value);
    value = clamp((value - 0.5) * linearContrast + 0.5, 0.0, 1.0);
    sat *= saturation;
    if (value <= 0.000001) c = float3(0.0);
    else if (sat <= 0.000001) c = float3(value);
    else {
        float hf = h * 6.0, chroma = value * sat;
        float f = hf - floor(hf), q = chroma * (1.0 - f), t = chroma * f;
        if (hf < 1.0) c = float3(chroma, t, 0.0);
        else if (hf < 2.0) c = float3(q, chroma, 0.0);
        else if (hf < 3.0) c = float3(0.0, chroma, t);
        else if (hf < 4.0) c = float3(0.0, q, chroma);
        else if (hf < 5.0) c = float3(t, 0.0, chroma);
        else c = float3(chroma, 0.0, q);
        c += value - dot(c, float3(0.299, 0.587, 0.114));
    }
    c = float3(filmOutput(c.r), filmOutput(c.g), filmOutput(c.b));
    return filmWrite(c, pixel.a, p);
}
"

        Private Const FatToniShader As String = "
uniform float3 highTone;
uniform float3 lowTone;
uniform float3 baseTone;
uniform float mids;
uniform float separation;
uniform float contrast;
half4 main(float2 p) {
    float4 pixel = filmRead(p);
    if (pixel.a <= 0.0) return half4(0.0);
    float3 c = float3(filmDomain(pixel.r), filmDomain(pixel.g), filmDomain(pixel.b));
    float a = clamp((c.r + c.g + c.b) / 3.0 + mids, 0.0, 1.0);
    a = clamp(a - separation * filmSin(2.0 * filmPi * a), 0.0, 1.0);
    float3 tone = highTone * a + lowTone * (1.0 - a);
    float3 screen = 1.0 - (1.0 - tone) * (1.0 - c);
    float3 overlay = c * screen + (1.0 - c) * tone * c;
    c = baseTone * (overlay * contrast + clamp(c + tone - 0.5, 0.0, 1.0) * (1.0 - contrast));
    c = float3(filmOutput(c.r), filmOutput(c.g), filmOutput(c.b));
    return filmWrite(c, pixel.a, p);
}
"
    End Class
End Namespace
