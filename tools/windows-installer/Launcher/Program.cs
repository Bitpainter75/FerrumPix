using System.Diagnostics;

var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "FerrumPix.exe")) {
    UseShellExecute = false,
    WorkingDirectory = AppContext.BaseDirectory
};
start.Environment["FERRUMPIX_PROFILE_ROOT"] = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPixDev", "Profile");
foreach (var argument in args) start.ArgumentList.Add(argument);
Process.Start(start);
