using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using FerrumPix.Services;
using FerrumPix.Views;

internal static class LocalizationChecks
{
    internal static void Run(Action<bool, string> check, ViewerView viewer, string savedPath)
    {
        string[] sources = {
            "Im vorhandenen Fenster öffnen",
            "Dateien im bereits geöffneten Fenster öffnen.",
            "Speicherordner für Videoaufnahmen",
            "Das Kamerasymbol speichert ein PNG. Bei gleichem Namen wird eine Nummer angehängt.",
            "Ordner auswählen…", "Wiedergabe/Pause (Space)",
            "Aktuelles Videobild als PNG speichern", "Lautstärke",
            "Bildschirmaufnahme gespeichert: {0}", "Bildschirmaufnahme",
            "Der Speicherordner für Aufnahmen muss ein absoluter Pfad sein.",
            "Zu viele Aufnahmen mit demselben Namen. Die Aufnahme konnte nicht gespeichert werden.",
            "Das Video wurde gewechselt oder ist noch nicht bereit.",
            "Das Videobild konnte nicht aufgenommen werden ({0})."
        };
        var resources = new ResourceManager("FerrumPix.Strings", typeof(LocalizationService).Assembly);
        foreach (var culture in new[] { "", "cs", "da", "de", "es", "fi", "fr", "hi", "id", "it", "ja", "ko", "nb", "nl", "pl", "pt", "ru", "sv", "th", "tr", "zh-CN" })
        {
            var set = resources.GetResourceSet(CultureInfo.GetCultureInfo(culture), true, false)!;
            var complete = sources.All(source => {
                var stem = Regex.Replace(source, "[^A-Za-z0-9]+", "_").Trim('_');
                if (stem.Length == 0) stem = "Text";
                if (stem.Length > 48) stem = stem[..48];
                var key = stem + "_" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source)))[..8].ToLowerInvariant();
                var value = set.GetString(key);
                return !string.IsNullOrWhiteSpace(value) &&
                    (!source.Contains("{0}") || string.Format(value, "sentinel {1}").Contains("sentinel {1}"));
            });
            check(complete, $"new video/settings strings and format placeholders exist in '{culture}' without fallback");
        }

        var settings = new SettingsView();
        var toggle = settings.GetLogicalDescendants().OfType<CheckBox>()
            .Single(control => Equals(control.Content, sources[0]));
        var volume = viewer.FindControl<FerrumPix.Controls.RoundSlider>("VideoVolumeSlider")!;
        var feedback = viewer.FindControl<TextBlock>("VideoCaptureSavedText")!;
        var setFeedback = typeof(ViewerView).GetMethod("SetCaptureSavedFeedback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var previousLanguage = LocalizationService.LanguageMode;
        try
        {
            // Reuse the same controls so a second language change must translate the original source.
            foreach (var (mode, label, volumeLabel, prefix) in new[] {
                ("Japanese", "既存ウィンドウで開く", "音量", "スクリーンショットを保存しました: "),
                ("German", "Im vorhandenen Fenster öffnen", "Lautstärke", "Bildschirmaufnahme gespeichert: "),
                ("English", "Open in the existing window", "Volume", "Saved Screenshot: ") })
            {
                LocalizationService.LanguageMode = mode;
                LocalizationService.ApplyTo(settings);
                LocalizationService.ApplyTo(viewer.FindControl<Avalonia.Controls.Primitives.Popup>("VideoVolumePopup")!.Child);
                check(Equals(toggle.Content, label) && ((string)ToolTip.GetTip(volume)!).Trim() == volumeLabel,
                    $"settings and popup translate after switching to {mode}");
                var pathWithBraces = savedPath + " {0}";
                setFeedback.Invoke(viewer, new object[] { pathWithBraces });
                check(feedback.Text == prefix + pathWithBraces && Equals(ToolTip.GetTip(feedback), feedback.Text),
                    $"{mode} capture feedback preserves the full path literally");
                try { VideoCaptureService.ResolveFolder("relative"); check(false, "relative capture path rejected"); }
                catch (ArgumentException error) { check(error.Message == LocalizationService.T(sources[10]), $"{mode} capture error is localized"); }
            }
        }
        finally
        {
            setFeedback.Invoke(viewer, new object[] { null! });
            LocalizationService.LanguageMode = previousLanguage;
        }
    }
}
