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
        using var bitmap = new RenderTargetBitmap(new PixelSize(736, 820), new Vector(96, 96));
        bitmap.Render(window);
        using (var stream = File.Create(path)) bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        window.Close();
        Console.WriteLine("Rendered actual editor panels: " + path);
    }
}
