using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClaudeSessionMonitoringOverlay;

public sealed class AppSettings
{
    public double Opacity { get; set; } = 0.9;      // whole overlay (Ctrl+1–5)
    public double BgOpacity { get; set; } = 1.0;    // card background
    public double CardOpacity { get; set; } = 1.0;  // rows, tiles, badges
    public double TextOpacity { get; set; } = 1.0;  // text, icons, numbers
    public double CtrlOpacity { get; set; } = 1.0;  // whole overlay while Ctrl is held
    public bool HookPromptDismissed { get; set; }
    public bool? TrackDesktop { get; set; }         // Claude Desktop hook switch
    public bool? TrackCli { get; set; }             // Claude Code (terminal) hook switch
    public string Theme { get; set; } = "dark"; // dark | light | system
    public string DoneSound { get; set; } = "chime";
    public string? DoneSoundFile { get; set; }
    public string InputSound { get; set; } = "knock";
    public string? InputSoundFile { get; set; }
    public double Volume { get; set; } = 0.6;
    public double? Left { get; set; }
    public double? Top { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Opts));
        }
        catch (Exception ex) { Log.Write("Save settings failed: " + ex.Message); }
    }
}

internal static class StartupEntry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ClaudeSessionMonitoringOverlay";

    /// <summary>Earlier builds used the Run value name "ClaudeOverlay". Move it to the new name, pointing at this exe.</summary>
    public static void MigrateLegacy()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue("ClaudeOverlay") is string)
            {
                k.SetValue(Name, "\"" + Environment.ProcessPath + "\"");
                k.DeleteValue("ClaudeOverlay", false);
            }
        }
        catch { }
    }

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, "\"" + Environment.ProcessPath + "\"");
        else k.DeleteValue(Name, false);
    }
}

internal static class Theme
{
    public static bool IsDark(string theme) => theme switch
    {
        "light" => false,
        "system" => !SystemUsesLight(),
        _ => true,
    };

    private static bool SystemUsesLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    public static Color RowColor { get; private set; }

    public static void Apply(AppSettings s)
    {
        bool dark = IsDark(s.Theme);
        // Background never goes fully to 0: a fully transparent pixel can't be clicked, even in Ctrl mode.
        double bg = Math.Clamp(s.BgOpacity, 0.01, 1);
        double card = Math.Clamp(s.CardOpacity, 0, 1);
        double text = Math.Clamp(s.TextOpacity, 0.15, 1);
        var r = Application.Current.Resources;
        void Set(string key, string dk, string lt, double a) => r[key] = Brush(dark ? dk : lt, a);

        // Overlay layers
        Set("Bg", "#191817", "#FFFFFF", bg);
        Set("Line", "#33322F", "#E3E1D8", bg);
        Set("RowBg", "#24231F", "#F6F5F0", card);
        Set("TileBg", "#22211E", "#F6F5F0", card);
        Set("ExBg", "#0C447C", "#E6F1FB", card);
        Set("PlBg", "#3C3489", "#EEEDFE", card);
        Set("AmBg", "#633806", "#FAEEDA", card);
        Set("Fg", "#F1EFE8", "#2C2C2A", text);
        Set("Muted", "#8F8D86", "#888780", text);
        Set("ExFg", "#B5D4F4", "#0C447C", text);
        Set("PlFg", "#CECBF6", "#3C3489", text);
        Set("AmFg", "#FAC775", "#633806", text);
        Set("Amber", "#EF9F27", "#BA7517", text);
        Set("Blue", "#378ADD", "#185FA5", text);
        Set("Green", "#639922", "#3B6D11", text);
        Set("Purple", "#7F77DD", "#534AB7", text);

        // Settings window: always solid
        Set("SBg", "#191817", "#FFFFFF", 1);
        Set("SFg", "#F1EFE8", "#2C2C2A", 1);
        Set("SMuted", "#8F8D86", "#888780", 1);
        Set("SLine", "#33322F", "#E3E1D8", 1);
        Set("SCtlBg", "#2A2926", "#F1EFE8", 1);

        var rc = (Color)ColorConverter.ConvertFromString(dark ? "#24231F" : "#F6F5F0");
        rc.A = (byte)Math.Round(255 * card);
        RowColor = rc;
    }

    public static SolidColorBrush Brush(string hex, double alpha = 1)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = (byte)Math.Round(c.A * Math.Clamp(alpha, 0, 1));
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
