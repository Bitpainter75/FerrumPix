using System.Reflection;
using System.Runtime.InteropServices;
using FerrumPix.Services;
using SkiaSharp;

// A package-free regression runner: compare against independently translated CTLs,
// then exercise the actual bitmap stages and recipe/cache integration.
static class Program
{
    const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    static int checks;
    static object Call(string name, params object[] args) =>
        typeof(ImageProcessor).GetMethod(name, Hidden)!.Invoke(null, args)!;
    static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }
    static double Decode(double x) => x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4);
    static double Encode(double x) => x <= .0031308 ? x * 12.92 : 1.055 * Math.Pow(x, 1 / 2.4) - .055;

    static ImageAdjustments Recipe() => new()
    {
        SiliconBonkEnabled = true, SiliconBonkHue = 240, SiliconBonkLuma = 30,
        SiliconBonkSaturation = 80, SiliconBonkMidpoint = 20, SiliconBonkContrast = 35,
        FatToniEnabled = true, FatToniHighlightHue = 35, FatToniHighlightSaturation = 40,
        FatToniShadowHue = 220, FatToniShadowSaturation = 50, FatToniSeparation = 60,
        FatToniMidpoint = -10, FatToniContrast = 85, FatToniBaseLuminance = 95
    };

    static void FormulaParity()
    {
        var random = new Random(271828);
        foreach (var group in new[] { "SiliconBonk", "FatToni" })
        for (int i = 0; i < 512; i++)
        {
            var a = Recipe();
            // Include controls' full ranges, their endpoints, and hue wrapping.
            double Next(double lo, double hi) => i % 8 == 0 ? lo : i % 8 == 1 ? hi : lo + random.NextDouble() * (hi - lo);
            a.SiliconBonkHue = (float)Next(0, 360); a.SiliconBonkLuma = (float)Next(-100, 100);
            a.SiliconBonkSaturation = (float)Next(0, 200); a.SiliconBonkMidpoint = (float)Next(-100, 100);
            a.SiliconBonkContrast = (float)Next(-50, 200); a.SiliconBonkPreserveDetails = i % 2 == 0;
            a.FatToniHighlightHue = (float)Next(0, 360); a.FatToniHighlightSaturation = (float)Next(0, 100);
            a.FatToniShadowHue = (float)Next(0, 360); a.FatToniShadowSaturation = (float)Next(0, 100);
            a.FatToniSeparation = (float)Next(0, 100); a.FatToniMidpoint = (float)Next(-100, 100);
            a.FatToniContrast = (float)Next(0, 100); a.FatToniBaseHue = (float)Next(0, 360);
            a.FatToniBaseSaturation = (float)Next(0, 100); a.FatToniBaseLuminance = (float)Next(0, 100);
            double r = random.NextDouble(), g = random.NextDouble(), b = random.NextDouble();
            if (i % 9 == 0) r = g = b = i % 18 == 0 ? 0 : 1;
            if (i % 11 == 0) { r = 1; g = 0; b = 0; }
            double er, eg, eb;
            if (group == "SiliconBonk")
                SiliconBonkCtl.ART_main(Decode(r), Decode(g), Decode(b), out er, out eg, out eb,
                    a.SiliconBonkHue, a.SiliconBonkLuma, a.SiliconBonkSaturation,
                    a.SiliconBonkMidpoint, a.SiliconBonkContrast, a.SiliconBonkPreserveDetails);
            else
                FatToniCtl.ART_main(Decode(r), Decode(g), Decode(b), out er, out eg, out eb,
                    a.FatToniHighlightHue, a.FatToniHighlightSaturation, a.FatToniShadowHue,
                    a.FatToniShadowSaturation, a.FatToniSeparation, a.FatToniMidpoint,
                    a.FatToniContrast, a.FatToniBaseHue, a.FatToniBaseSaturation, a.FatToniBaseLuminance);
            var pixel = (Delegate)Call(group + "Pixel", a);
            object[] values = { r, g, b };
            pixel.DynamicInvoke(values);
            double[] expected = { Encode(er), Encode(eg), Encode(eb) };
            for (int c = 0; c < 3; c++)
                Check(double.IsFinite((double)values[c]) && Math.Abs((double)values[c] - expected[c]) < 1e-7,
                    $"{group} differs from CTL at sample {i}, channel {c}: {values[c]} vs {expected[c]}");
        }
    }

    static SKBitmap Pixels(SKAlphaType alphaType)
    {
        var image = new SKBitmap(16, 8, SKColorType.Bgra8888, alphaType);
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
            image.SetPixel(x, y, new SKColor((byte)(x * 17), (byte)(y * 31), (byte)(255 - x * 13), (byte)(x * 17)));
        return image;
    }

    static void BitmapAndDeep()
    {
        var adj = Recipe();
        foreach (var alphaType in new[] { SKAlphaType.Premul, SKAlphaType.Unpremul })
        using (var source = Pixels(alphaType))
        foreach (var group in new[] { "SiliconBonk", "FatToni" })
        {
            var pixel = Call(group + "Pixel", adj);
            using var result = (SKBitmap)Call("ApplyFilmEffect", source, pixel);
            var before = new byte[source.ByteCount]; var after = new byte[result.ByteCount];
            Marshal.Copy(source.GetPixels(), before, 0, before.Length);
            Marshal.Copy(result.GetPixels(), after, 0, after.Length);
            for (int i = 0; i < before.Length; i += 4)
            {
                Check(after[i + 3] == before[i + 3], "Alpha changed");
                if (alphaType == SKAlphaType.Premul)
                    Check(after[i] <= after[i + 3] && after[i + 1] <= after[i + 3] && after[i + 2] <= after[i + 3], "Invalid premultiplied pixel");
            }
        }
        // Active grain must be toned, while deep values retain precision between 8-bit steps.
        adj.Grain = 30; adj.GrainSize = 20; adj.GrainFrequency = 40;
        using var deep = new SKBitmap(16, 8, SKColorType.Rgba16161616, SKAlphaType.Unpremul);
        var samples = Enumerable.Range(0, 128).SelectMany(i => new ushort[] { (ushort)(1234 + i * 211), (ushort)(4567 + i * 73), (ushort)(14567 + i * 89), 65535 }).ToArray();
        Marshal.Copy(samples.Select(v => unchecked((short)v)).ToArray(), 0, deep.GetPixels(), samples.Length);
        using var first = (SKBitmap)Call("ApplyFilmEffect", deep, Call("SiliconBonkPixel", adj));
        using var grained = (SKBitmap)Call("ApplyGrainDeep", first, .3f, .2f, .4f, 0f);
        using var second = (SKBitmap)Call("ApplyFilmEffect", grained, Call("FatToniPixel", adj));
        var output = new short[samples.Length]; Marshal.Copy(second.GetPixels(), output, 0, output.Length);
        Check(output.Where((_, i) => i % 4 != 3).Any(v => (ushort)v % 257 != 0), "Deep output collapsed to 8 bits");
        using var shallow = (SKBitmap)Call("ShallowFromDeep", deep, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        // Ownership of this separate deep bitmap passes to the pipeline.
        var companion = deep.Copy(); Call("AttachDeep", shallow, companion);
        object[] args = { shallow, adj, false };
        using var processed = (SKBitmap)typeof(ImageProcessor).GetMethod("ApplyDetailAdjustmentStagesCore", Hidden)!.Invoke(null, args)!;
        var processedDeep = (SKBitmap)Call("DeepOf", processed);
        Check(processedDeep != null, "Deep companion lost in actual stage chain");
        var actual = new short[output.Length]; Marshal.Copy(processedDeep!.GetPixels(), actual, 0, actual.Length);
        Check(actual.SequenceEqual(output), "Actual chain does not run SiliconBonk, Grain, then FatToni");
        Call("DropDeep", processed); Call("DropDeep", shallow);
        using var tonedBeforeGrain = (SKBitmap)Call("ApplyFilmEffect", first, Call("FatToniPixel", adj));
        using var oldOrder = (SKBitmap)Call("ApplyGrainDeep", tonedBeforeGrain, .3f, .2f, .4f, 0f);
        var oldSamples = new short[output.Length]; Marshal.Copy(oldOrder.GetPixels(), oldSamples, 0, oldSamples.Length);
        Check(!oldSamples.SequenceEqual(output), "Fixture cannot distinguish toned grain from a neutral overlay");
        using var ordinary = Pixels(SKAlphaType.Unpremul);
        using var mono = (SKBitmap)Call("ApplyFilmEffect", ordinary, Call("SiliconBonkPixel", adj));
        using var grain8 = (SKBitmap)Call("ApplyGrain", mono, .3f, .2f, .4f, 0f);
        using var expected8 = (SKBitmap)Call("ApplyFilmEffect", grain8, Call("FatToniPixel", adj));
        object[] args8 = { ordinary, adj, false };
        using var actual8 = (SKBitmap)typeof(ImageProcessor).GetMethod("ApplyDetailAdjustmentStagesCore", Hidden)!.Invoke(null, args8)!;
        var bytesExpected = new byte[expected8.ByteCount]; var bytesActual = new byte[actual8.ByteCount];
        Marshal.Copy(expected8.GetPixels(), bytesExpected, 0, bytesExpected.Length);
        Marshal.Copy(actual8.GetPixels(), bytesActual, 0, bytesActual.Length);
        Check(bytesExpected.SequenceEqual(bytesActual), "8-bit preview does not tone the grain after SiliconBonk");
    }

    static void RecipesAndCaches()
    {
        var adj = Recipe(); var clone = adj.Clone();
        foreach (var p in typeof(ImageAdjustments).GetProperties().Where(p => p.Name.StartsWith("SiliconBonk") || p.Name.StartsWith("FatToni")))
            Check(Equals(p.GetValue(adj), p.GetValue(clone)), "Clone omitted " + p.Name);
        var fpx = typeof(ImageProcessor).Assembly.GetType("FerrumPix.Services.FpxService")!;
        var json = fpx.GetMethod("SerializeAdjustments", Hidden)!.Invoke(null, new object[] { adj });
        var loaded = (ImageAdjustments)fpx.GetMethod("DeserializeAdjustments", Hidden)!.Invoke(null, new[] { json })!;
        Check(loaded.FatToniEnabled && loaded.SiliconBonkEnabled && loaded.SiliconBonkSaturation == 80, "Recipe persistence failed");
        var old = (ImageAdjustments)fpx.GetMethod("DeserializeAdjustments", Hidden)!.Invoke(null, new object[] { "{}" })!;
        Check(!old.SiliconBonkEnabled && !old.FatToniEnabled, "Old recipe enables effects");
        var key = Call("ComputeBaseKeyCore", adj, false);
        var toneKey = Call("ComputeToneCacheKey", adj);
        adj.FatToniBaseHue++;
        Check(!Equals(key, Call("ComputeBaseKeyCore", adj, false)), "Effect omitted from cache key");
        Check(Equals(toneKey, Call("ComputeToneCacheKey", adj)), "Late effect invalidates tone cache");
        var preSharpen = (ImageAdjustments)Call("WithoutSharpening", adj);
        Check(!preSharpen.SiliconBonkEnabled && !preSharpen.FatToniEnabled, "Late effects leak into sharpen mask");
        var upgraded = AppSettingsService.NormalizeEditorCompactGroupOrder("light,vignette,grain").Split(',');
        var at = Array.IndexOf(upgraded, "vignette");
        Check(upgraded[at + 1] == "siliconbonk" && upgraded[at + 2] == "grain" && upgraded[at + 3] == "fattoni", "Existing compact layout did not insert film tools in order");
        var custom = AppSettingsService.NormalizeEditorCompactGroupOrder("fattoni,grain,siliconbonk,vignette");
        Check(custom.StartsWith("fattoni,grain,siliconbonk,vignette"), "Custom compact order was overwritten");
    }

    static void ParameterCompatibility()
    {
        var defaults = new ImageAdjustments();
        foreach (var field in typeof(FilmEffectParameters).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.FieldType == typeof(FilmParameterDefinition)))
        {
            var definition = (FilmParameterDefinition)field.GetValue(null)!;
            Check(Equals(typeof(ImageAdjustments).GetProperty(field.Name)!.GetValue(defaults), definition.DefaultValue),
                "Recipe default differs from shared definition: " + field.Name);
            Check(definition.ClampValue(double.NaN) == definition.DefaultValue &&
                  definition.ClampValue(double.PositiveInfinity) == definition.DefaultValue,
                "Non-finite parameter did not use its default: " + field.Name);
        }
        var sb = FilmEffectParameters.SiliconBonkContrast;
        var ft = FilmEffectParameters.FatToniContrast;
        Check(sb.ClampValue(500) == 200 && sb.ClampValue(-100) == -50, "SB current contrast limits not enforced");
        Check(ft.ClampValue(200) == 100 && ft.ClampValue(-50) == 0, "FT current contrast limits not enforced");
        var fpx = typeof(ImageProcessor).Assembly.GetType("FerrumPix.Services.FpxService")!;
        var method = fpx.GetMethod("DeserializeAdjustments", Hidden)!;
        var loaded = (ImageAdjustments)method.Invoke(null, new object[] {
            "{\"SiliconBonkEnabled\":true,\"SiliconBonkContrast\":500,\"FatToniContrast\":-50,\"SiliconBonkLuma\":900}" })!;
        Check(loaded.SiliconBonkContrast == 200 && loaded.FatToniContrast == 0, "Recipe loader retained out-of-range contrast");
        Check(loaded.SiliconBonkLuma == 100, "Recipe loader retained out-of-range luminance");
        var nested = (ImageAdjustments)method.Invoke(null, new object[] {
            "{\"Annotations\":[{\"Adjustments\":{\"FatToniContrast\":200,\"FatToniBaseSaturation\":900}}]," +
            "\"MaskedAdjustmentLayers\":[{\"Adjustments\":{\"SiliconBonkContrast\":500,\"SiliconBonkHue\":900}}]}" })!;
        Check(nested.Annotations[0].Adjustments.FatToniContrast == 100 &&
              nested.Annotations[0].Adjustments.FatToniBaseSaturation == 100, "Object recipe bypassed current limits");
        Check(nested.MaskedAdjustmentLayers[0].Adjustments.SiliconBonkContrast == 200 &&
              nested.MaskedAdjustmentLayers[0].Adjustments.SiliconBonkHue == 360, "Masked-layer recipe bypassed current limits");
        // Recipes constructed directly in memory must obey the same contract as loaded files.
        var invalid = Recipe(); invalid.SiliconBonkContrast = 500; invalid.FatToniContrast = -50;
        Check(invalid.SiliconBonkContrast == 200 && invalid.FatToniContrast == 0, "Programmatically built recipe bypassed current limits");
        var bounded = invalid.Clone(); bounded.SiliconBonkContrast = 200; bounded.FatToniContrast = 0;
        foreach (var group in new[] { "SiliconBonk", "FatToni" })
        {
            object[] bad = { .2, .4, .6 }, good = { .2, .4, .6 };
            ((Delegate)Call(group + "Pixel", invalid)).DynamicInvoke(bad);
            ((Delegate)Call(group + "Pixel", bounded)).DynamicInvoke(good);
            Check(bad.SequenceEqual(good), "Renderer bypassed limits for " + group);
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            FormulaParity(); BitmapAndDeep(); RecipesAndCaches(); ParameterCompatibility();
            Console.WriteLine($"Passed {checks} checks: CTL parity, alpha, deep precision/order, recipes and caches.");
            GpuChecks.Run(args.Contains("--gpu-device"));
            if (args.Length == 2 && args[0] == "--preview") Preview.Render(args[1], Recipe());
            return 0;
        }
        catch (Exception ex)
        {
            // Report failures to automation without leaving a Windows crash dialog holding DLLs.
            Console.Error.WriteLine(ex); return 1;
        }
    }
}
