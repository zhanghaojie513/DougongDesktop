using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

Environment.SetEnvironmentVariable("DOUGONG_UI_SELFTEST", "1");

AppBuilder.Configure<DougongDesktop.App.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var win = new DougongDesktop.App.Views.MainWindow();
win.Show();
for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(60); }
win.Measure(new Size(1280, 820));
win.Arrange(new Rect(0, 0, 1280, 820));
Dispatcher.UIThread.RunJobs();

var f = win.CaptureRenderedFrame();
f?.Save(args[0], new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
Console.WriteLine("已保存");
