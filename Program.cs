using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ClaudeOverlay;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Hook mode: Claude Code runs this with the event JSON on stdin. Must stay fast and silent.
        if (args.Length > 0 && args[0] == "--hook") return HookWriter.Run();
        if (args.Length > 0 && args[0] == "--install-hook") return HookInstaller.Install(Environment.ProcessPath!) ? 0 : 1;
        if (args.Length > 0 && args[0] == "--uninstall-hook") return HookInstaller.Uninstall() ? 0 : 1;

        return RunGui();
    }

    // Kept in its own method so hook mode never loads the WPF assemblies (keeps each hook call fast).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunGui()
    {
        using var mutex = new Mutex(true, "ClaudeOverlay.SingleInstance", out bool created);
        if (!created) return 0;

        var app = new App();
        app.Run();
        return 0;
    }
}

public sealed class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;

        Directory.CreateDirectory(Paths.Data);
        var settings = AppSettings.Load();
        HookSwitches.Migrate(settings);
        Theme.Apply(settings);
        Sounds.EnsureBuiltins();

        var overlay = new OverlayWindow(settings);
        overlay.Show();
        if (Array.IndexOf(e.Args, "--settings") >= 0) overlay.OpenSettings();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled: " + e.Exception);
        e.Handled = true;
    }
}

internal static class Paths
{
    public static readonly string Data =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeOverlay");
    public static string State => Path.Combine(Data, "state");
    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string SoundsDir => Path.Combine(Data, "sounds");
    public static string LogFile => Path.Combine(Data, "overlay.log");

    public static readonly string SessionsRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");

    public static readonly string ClaudeSettings =
        Environment.GetEnvironmentVariable("CLAUDE_OVERLAY_SETTINGS_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
}

internal static class Log
{
    public static void Write(string msg)
    {
        try
        {
            var f = new FileInfo(Paths.LogFile);
            if (f.Exists && f.Length > 512 * 1024) f.Delete();
            File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }
}
