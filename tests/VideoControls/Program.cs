using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using FerrumPix.Controls;
using FerrumPix.Services;
using FerrumPix.ViewModels;

Environment.SetEnvironmentVariable("FERRUMPIX_PROFILE_ROOT", Path.Combine(Path.GetTempPath(), "FerrumPix-tests", Guid.NewGuid().ToString()));
AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

using var player = new MpvPlayer(false); // no native window needed for the UI-state regression checks
var vm = new ViewerViewModel(null);
Set(vm, "_mediaPlayer", player);
vm.CurrentImagePath = "test.mp4";
vm.VideoDurationSeconds = 60;
vm.VideoPositionSeconds = 25;
Check(vm.TryHandleVideoKey(Key.Right, KeyModifiers.None) && vm.VideoPositionSeconds == 35, "Right seeks +10 seconds");
Check(vm.TryHandleVideoKey(Key.Left, KeyModifiers.None) && vm.VideoPositionSeconds == 25, "Left seeks -10 seconds");
Check(vm.TryHandleVideoKey(Key.Space, KeyModifiers.None), "Space handled for video");
Check(!vm.TryHandleVideoKey(Key.PageDown, KeyModifiers.None), "PageDown remains file navigation");
Check(!vm.TryHandleVideoKey(Key.Right, KeyModifiers.Control), "modified keys preserved");
vm.VideoPositionSeconds = 4;
vm.SkipVideo(-10);
Check(vm.VideoPositionSeconds == 0, "backward seek clamps at start");
vm.VideoPositionSeconds = 58;
vm.SkipVideo(10);
Check(vm.VideoPositionSeconds == 60, "forward seek clamps at duration");

Call(vm, "OnVideoTimeChanged", 3d); // queued before gesture
vm.BeginVideoScrub();
vm.UpdateVideoScrub(42);
Dispatcher.UIThread.RunJobs();
Check(vm.VideoPositionSeconds == 42, "pre-gesture queued position cannot overwrite scrub");
Thread.Sleep(300);
Call(vm, "OnVideoTimeChanged", 5d);
Dispatcher.UIThread.RunJobs();
Check(vm.VideoPositionSeconds == 42, "playback position cannot overwrite drag after 250ms");
vm.EndVideoScrub(44);
Check(vm.VideoPositionSeconds == 44, "release commits final position");
vm.BeginVideoScrub();
vm.CurrentImagePath = "other.mp4";
vm.VideoPositionSeconds = 0;
vm.EndVideoScrub(55);
Check(vm.VideoPositionSeconds == 0, "release cannot seek a different file");
vm.VideoPositionSeconds = 60;
Call(vm, "OnVideoEndReached", 0, 0);
Call(vm, "OnVideoShortcutRequested", player, "other.mp4", "LEFT");
Dispatcher.UIThread.RunJobs();
Check(vm.VideoPositionSeconds == 50 && !(bool)typeof(ViewerViewModel).GetField("_isVideoEnded", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!, "native rewind after queued EOF restores video state");
Call(vm, "OnVideoShortcutRequested", player, "old.mp4", "LEFT");
Dispatcher.UIThread.RunJobs();
Check(vm.VideoPositionSeconds == 50, "native shortcut from previous file is ignored");
vm.CurrentImagePath = "image.png";
Check(!vm.TryHandleVideoKey(Key.Space, KeyModifiers.None) && !vm.TryHandleVideoKey(Key.Left, KeyModifiers.None), "image keyboard behavior preserved");

var slider = new RoundSlider { Minimum = 0, Maximum = 60, Width = 300, Height = 40, ResetOnDoubleClick = false };
var window = new Window { Width = 300, Height = 40, Content = slider };
var started = 0; var edited = 0; var completed = 0; double committed = 0;
slider.InteractionStarted += () => started++;
slider.ValueEdited += _ => edited++;
slider.InteractionCompleted += value => { completed++; committed = value; };
window.Show();
window.MouseDown(new Point(50,20), MouseButton.Left);
window.MouseMove(new Point(240,20), Avalonia.Input.RawInputModifiers.LeftMouseButton);
window.MouseUp(new Point(240,20), MouseButton.Left);
Check(started == 1 && edited >= 2 && completed == 1 && committed > 40, "pointer drag previews and commits once despite capture loss");
window.MouseDown(new Point(200,20), MouseButton.Left);
window.MouseUp(new Point(200,20), MouseButton.Left);
Check(slider.Value > 30, "rapid second click does not reset video to zero");
window.Close();

if (args.Length > 0)
{
    Environment.SetEnvironmentVariable("FERRUMPIX_VIDEO_RENDER_API", "1");
    using var native = new MpvPlayer(false);
    double time = -1, duration = 0; string failure = null;
    native.TimeChanged += value => Volatile.Write(ref time, value);
    native.DurationChanged += value => Volatile.Write(ref duration, value);
    native.InitializationFailed += error => failure = error.ToString();
    native.Start(); native.Load(Path.GetFullPath(args[0])); native.Play();
    void WaitFor(Func<bool> predicate, string name)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < until && failure == null) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Check(failure == null && predicate(), name + (failure == null ? "" : ": " + failure));
    }
    WaitFor(() => Volatile.Read(ref duration) > 25 && Volatile.Read(ref time) >= 0, "bundled libmpv loads generated video");
    var nativeVm = new ViewerViewModel(null) { CurrentImagePath = Path.GetFullPath(args[0]) };
    Set(nativeVm, "_mediaPlayer", native);
    var paused = false;
    native.PauseChanged += value => Volatile.Write(ref paused, value);
    nativeVm.TryHandleVideoKey(Key.Space, KeyModifiers.None);
    WaitFor(() => Volatile.Read(ref paused), "Space pauses actual libmpv playback");
    nativeVm.TryHandleVideoKey(Key.Space, KeyModifiers.None);
    WaitFor(() => !Volatile.Read(ref paused), "Space resumes actual libmpv playback");
    string requestedKey = null;
    native.ShortcutRequested += (source, path, key) => { if (ReferenceEquals(source, native) && path == Path.GetFullPath(args[0])) requestedKey = key; };
    var handle = (IntPtr)typeof(MpvPlayer).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(native)!;
    typeof(MpvPlayer).GetMethod("CommandRaw", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { handle, new[] { "script-message", "ferrumpix-key", "LEFT" } });
    WaitFor(() => requestedKey == "LEFT", "native client message delivers shortcut with file identity");
    for (var i = 0; i < 100; i++) native.Seek(i % 25, false);
    native.Seek(18, true);
    WaitFor(() => Volatile.Read(ref time) >= 17.8 && Volatile.Read(ref time) < 20, "seek burst converges to final exact target");
    native.Seek(2, true);
    WaitFor(() => Volatile.Read(ref time) >= 1.8 && Volatile.Read(ref time) < 4, "backward exact seek works");
    var ended = false;
    native.EndReached += (reason, error) => { if (reason == 0) Volatile.Write(ref ended, true); };
    native.Seek(30, true);
    WaitFor(() => Volatile.Read(ref ended), "end of video reported");
    Volatile.Write(ref time, -1);
    native.Seek(10, true);
    WaitFor(() => Volatile.Read(ref time) >= 9.8 && Volatile.Read(ref time) < 12, "rewind remains possible after reaching end");
    native.Stop();
}
Console.WriteLine($"{passed} checks passed.");

