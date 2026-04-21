using System.IO;
using System.Windows;
using Serilog;

namespace NwdViewer.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NwdViewer", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug()
            .WriteTo.File(Path.Combine(logDir, "nwdviewer-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("NwdViewer starting.");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("NwdViewer exiting.");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
