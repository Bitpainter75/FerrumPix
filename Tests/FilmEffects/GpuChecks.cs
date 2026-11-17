using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using FerrumPix.Services;
using SkiaSharp;

static class GpuChecks
{
    const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly Type Processor = typeof(ImageProcessor);
    static int checks, maxDifference;
    static object Call(string name, params object[] args) => Processor.GetMethod(name, Hidden)!.Invoke(null, args)!;
    static void Flag(string name, bool value) => Processor.GetField(name, Hidden)!.SetValue(null, value);
    static byte[] Bytes(SKBitmap image)
    {
        var bytes = new byte[image.ByteCount]; Marshal.Copy(image.GetPixels(), bytes, 0, bytes.Length); return bytes;
    }
    static void Compare(SKBitmap expected, SKBitmap actual, int tolerance, string context)
    {
        var left = Bytes(expected); var right = Bytes(actual);
        if (left.Length != right.Length) throw new Exception(context + ": dimensions changed");
        for (int i = 0; i < left.Length; i++)
        {
            int difference = Math.Abs(left[i] - right[i]);
            maxDifference = Math.Max(maxDifference, difference); checks++;
            if (difference > (i % 4 == 3 ? 0 : tolerance))
                throw new Exception($"{context}: byte {i}: CPU {left[i]}, shader {right[i]}, difference {difference}");
            if (i % 4 != 3 && right[i] > right[i - i % 4 + 3])
                throw new Exception(context + ": invalid premultiplied pixel");
        }
    }
    static SKBitmap Source(SKColorType colorType, bool opaque)
    {
        var image = new SKBitmap(64, 32, colorType, opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        var random = new Random(314159);
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            byte a = opaque ? (byte)255 : new byte[] { 0, 1, 2, 31, 64, 127, 128, 200, 254, 255 }[(x + y) % 10];
            byte r = (byte)random.Next(256), g = (byte)random.Next(256), b = (byte)random.Next(256);
            if (y < 8) r = g = b = (byte)(x * 255 / 63); // smooth ramps and both endpoints
            if (y == 8) { r = 255; g = b = 0; }
            if (y == 9) { g = 255; r = b = 0; }
            if (y == 10) { b = 255; r = g = 0; }
            image.SetPixel(x, y, new SKColor(r, g, b, a));
        }
        return image;
    }
    static SKBitmap Draw(string group, ImageAdjustments adj, SKBitmap source, bool hardware)
    {
        var stage = (Func<GRContext, SKImage, SKSurface>)Call(group + "OnGpu", adj);
        return DrawStage(stage, source, hardware);
    }
    static SKBitmap DrawStage(Func<GRContext, SKImage, SKSurface> stage, SKBitmap source, bool hardware)
    {
        var image = SKImage.FromBitmap(source);
        SKBitmap Work(GRContext gr)
        {
            using var surface = stage(gr, image);
            var result = new SKBitmap(source.Info);
            if (!surface.ReadPixels(result.Info, result.GetPixels(), result.RowBytes, 0, 0))
            { result.Dispose(); throw new Exception("Shader readback failed"); }
            return result;
        }
        if (!hardware)
        {
            using (image) return Work(null!);
        }
        // The service owns the image even on timeout, so no caller-side disposal can race it.
        SKBitmap output = null!;
        if (!GpuImageService.TryRun<SKBitmap>(Work, ref output, image))
            throw new Exception("GPU execution failed: " + GpuImageService.FailureReason);
        return output;
    }

    public static int Run(bool hardware = false)
    {
        if (hardware)
        {
            AppBuilder.Configure<FerrumPix.App>().UsePlatformDetect().SetupWithoutStarting();
            GpuImageService.Enabled = true;
            if (!GpuImageService.IsAvailable) throw new Exception("Hardware GPU unavailable: " + GpuImageService.FailureReason);
            Console.WriteLine("Hardware renderer: " + GpuImageService.Renderer);
        }
        try
        {
            foreach (var color in new[] { SKColorType.Bgra8888, SKColorType.Rgba8888 })
            foreach (bool opaque in new[] { false, true })
            using (var source = Source(color, opaque))
            for (int i = 0; i < 12; i++)
            {
                var adj = new ImageAdjustments
                {
                    SiliconBonkEnabled = true, SiliconBonkHue = new[] { 0, 40, 180, 360 }[i % 4],
                    SiliconBonkLuma = new[] { -100, 0, 100 }[i % 3], SiliconBonkSaturation = new[] { 0, 80, 200 }[i % 3],
                    SiliconBonkMidpoint = new[] { -100, 0, 100 }[(i / 3) % 3], SiliconBonkContrast = new[] { -50, 0, 100, 200 }[i % 4],
                    SiliconBonkPreserveDetails = i % 2 == 0,
                    FatToniEnabled = true, FatToniHighlightHue = i * 30, FatToniHighlightSaturation = i % 2 == 0 ? 0 : 75,
                    FatToniShadowHue = 360 - i * 30, FatToniShadowSaturation = 100,
                    FatToniBaseHue = 40, FatToniBaseSaturation = i % 3 == 0 ? 50 : 0,
                    FatToniBaseLuminance = i % 4 == 0 ? 40 : 100, FatToniMidpoint = new[] { -100, 0, 100 }[i % 3],
                    FatToniSeparation = new[] { 0, 50, 100 }[i % 3], FatToniContrast = new[] { 0, 85, 100 }[(i / 3) % 3]
                };
                foreach (var group in new[] { "SiliconBonk", "FatToni" })
                {
                    using var cpu = (SKBitmap)Call("ApplyFilmEffect", source, Call(group + "Pixel", adj));
                    using var shader = Draw(group, adj, source, hardware);
                    Compare(cpu, shader, 1, $"{group}, {color}, opaque={opaque}, preset={i}");
                }
            }
            Console.WriteLine($"Individual film-stage maximum difference: {maxDifference}/255.");
            SessionAndFallback(hardware);
            Console.WriteLine($"Passed {checks} {(hardware ? "hardware GPU" : "raster shader")} byte checks; maximum difference {maxDifference}/255.");
            return checks;
        }
        finally
        {
            Flag("DetailStageShadersOnRaster", false);
            Flag("FailNextDetailDownloadForCheck", false);
            if (hardware) GpuImageService.Enabled = false;
        }
    }

    static void SessionAndFallback(bool hardware)
    {
        using var source = Source(SKColorType.Bgra8888, false);
        var adj = new ImageAdjustments
        {
            SiliconBonkEnabled = true, SiliconBonkHue = 40, SiliconBonkLuma = 20, SiliconBonkContrast = 10,
            Grain = 20, GrainSize = 15, GrainFrequency = 30,
            FatToniEnabled = true, FatToniHighlightHue = 40, FatToniHighlightSaturation = 20,
            FatToniShadowHue = 220, FatToniShadowSaturation = 30
        };
        SKBitmap Render() => (SKBitmap)Call("ApplyDetailAdjustmentStagesCore", source, adj, false);
        // Exercise the actual session and device-loss replay even on this small fixture.
        var minimum = Processor.GetField("GpuMinimumPixels", Hidden)!;
        int previousMinimum = (int)minimum.GetValue(null)!;
        minimum.SetValue(null, 0);
        GpuImageService.Enabled = false;
        try
        {
            using var cpu = Render();
            using var monoCpu = (SKBitmap)Call("ApplyFilmEffect", source, Call("SiliconBonkPixel", adj));
            using var grainCpu = (SKBitmap)Call("ApplyGrain", monoCpu, .2f, .15f, .3f, 0f);
            var grainStage = (Func<GRContext, SKImage, SKSurface>)Call("GrainOnGpu", monoCpu, .2f, .15f, .3f, 0f);
            using var grainShader = DrawStage(grainStage, monoCpu, hardware);
            Compare(grainCpu, grainShader, 1, "Grain on identical film-stage input");
            Flag("DetailStageShadersOnRaster", !hardware);
            GpuImageService.Enabled = hardware;
            // Each quantized stage is checked separately within one byte above. Rounding can
            // accumulate across all three passes, particularly at translucent edges.
            using var shader = Render(); Compare(cpu, shader, 3, "SiliconBonk/Grain/FatToni session");
            using var monoGpu = Draw("SiliconBonk", adj, source, hardware);
            using var grainGpu = DrawStage(grainStage, monoGpu, hardware);
            using var tonedGpu = Draw("FatToni", adj, grainGpu, hardware);
            Compare(tonedGpu, shader, 0, "Resident session preserves SiliconBonk/Grain/FatToni order");
            Flag("FailNextDetailDownloadForCheck", true);
            using var recovered = Render(); Compare(cpu, recovered, 0, "Failed download CPU replay");
            if ((bool)Processor.GetField("FailNextDetailDownloadForCheck", Hidden)!.GetValue(null)!)
                throw new Exception("Fallback test did not exercise a GPU download");
        }
        finally { minimum.SetValue(null, previousMinimum); }
    }
}
