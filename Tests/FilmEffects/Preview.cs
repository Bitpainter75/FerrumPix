using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FerrumPix.Controls;
using FerrumPix.Controls.EditorPanels;
using FerrumPix.Services;
using FerrumPix.ViewModels;

static class Preview
{
    // Real panels, styles, localization and view model; no application window or photo is edited.
    public static void Render(string path, ImageAdjustments recipe)
    {
        AppBuilder.Configure<FerrumPix.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia().SetupWithoutStarting();
        LocalizationService.LanguageMode = "English";
        RoundSlider.ColoredTracks = true;
        var vm = new EditorViewModel(null!);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(EditorViewModel).GetMethod("ReadFilmEffectsFrom", instance)!.Invoke(vm, new object[] { recipe });
        var saved = new ImageAdjustments();
        typeof(EditorViewModel).GetMethod("WriteFilmEffectsInto", instance)!.Invoke(vm, new object[] { saved });
        if (!saved.SiliconBonkEnabled || saved.FatToniContrast != recipe.FatToniContrast)
            throw new Exception("Editor recipe binding failed");
        var left = new SiliconBonkPanel(); var right = new FatToniPanel();
        foreach (var panel in new UserControl[] { left, right })
        {
            foreach (var expander in panel.GetLogicalDescendants().OfType<Expander>()) expander.IsExpanded = true;
            panel.Width = 350;
            panel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        }
        var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12) };
        content.Children.Add(left); content.Children.Add(right);
        var window = new Window { Width = 736, Height = 820, DataContext = vm, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        LocalizationService.ApplyTo(window);
        LocalizationService.ApplyToVisualTree(window);
        Dispatcher.UIThread.RunJobs();
        // Assert that the visible controls actually received their values and colored tracks.
        var sliders = content.GetLogicalDescendants().OfType<RoundSlider>().ToArray();
        if (sliders.Length != 15 || sliders.Count(s => s.ColorTrack != null) != 11)
            throw new Exception($"Panel slider bindings failed: {sliders.Length} sliders / {sliders.Count(s => s.ColorTrack != null)} color tracks");
        var sbSliders = left.GetLogicalDescendants().OfType<RoundSlider>().ToArray();
        var ftSliders = right.GetLogicalDescendants().OfType<RoundSlider>().ToArray();
        if (sbSliders[0].Maximum != FilmEffectParameters.SiliconBonkHue.Maximum ||
            sbSliders[0].DefaultValue != FilmEffectParameters.SiliconBonkHue.DefaultValue ||
            sbSliders[4].Minimum != FilmEffectParameters.SiliconBonkContrast.Minimum ||
            sbSliders[4].Maximum != FilmEffectParameters.SiliconBonkContrast.Maximum ||
            ftSliders[9].Minimum != FilmEffectParameters.FatToniContrast.Minimum ||
            ftSliders[9].Maximum != FilmEffectParameters.FatToniContrast.Maximum)
            throw new Exception("Panel limits/defaults differ from shared parameter definitions");
        // Two-way control bindings must agree with the loader's current limits.
        var outside = recipe.Clone(); outside.SiliconBonkContrast = 500; outside.FatToniContrast = -50;
        var read = typeof(EditorViewModel).GetMethod("ReadFilmEffectsFrom", instance)!;
        var write = typeof(EditorViewModel).GetMethod("WriteFilmEffectsInto", instance)!;
        read.Invoke(vm, new object[] { outside }); Dispatcher.UIThread.RunJobs();
        write.Invoke(vm, new object[] { saved });
        if (saved.SiliconBonkContrast != 200 || saved.FatToniContrast != 0)
            throw new Exception("Attached controls bypassed current contrast limits");
        typeof(EditorViewModel).GetMethod("ResetSiliconBonkValues", instance)!.Invoke(vm, null);
        typeof(EditorViewModel).GetMethod("ResetFatToniValues", instance)!.Invoke(vm, null);
        Dispatcher.UIThread.RunJobs(); write.Invoke(vm, new object[] { saved });
        if (saved.SiliconBonkContrast != FilmEffectParameters.SiliconBonkContrast.DefaultValue ||
            saved.FatToniContrast != FilmEffectParameters.FatToniContrast.DefaultValue)
            throw new Exception("Attached controls changed reset defaults");
        read.Invoke(vm, new object[] { recipe }); Dispatcher.UIThread.RunJobs();
        using var bitmap = new RenderTargetBitmap(new PixelSize(736, 820), new Vector(96, 96));
        bitmap.Render(window);
        using (var stream = File.Create(path)) bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        window.Close();
        Console.WriteLine("Rendered actual editor panels: " + path);
    }
}
