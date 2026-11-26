using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;

namespace ClaudeSessionMonitoringOverlay;

/// <summary>Built-in sounds are synthesized once into small WAV files; custom sounds can be any WAV/MP3 path.</summary>
internal static class Sounds
{
    private record Note(double Freq, double Delay, double Length, bool Triangle = false);

    private static readonly Dictionary<string, Note[]> Defs = new()
    {
        ["chime"] = new[] { new Note(784, 0, .45), new Note(1175, .09, .45), new Note(1568, .18, .6) },
        ["ping"] = new[] { new Note(1500, 0, .3), new Note(1500, .13, .35) },
        ["blip"] = new[] { new Note(520, 0, .25), new Note(660, .1, .3) },
        ["knock"] = new[] { new Note(330, 0, .2, true), new Note(330, .15, .22, true) },
    };

    public static readonly (string key, string label)[] Choices =
    {
        ("chime", "Chime"), ("ping", "Ping"), ("blip", "Soft blip"), ("knock", "Knock"),
        ("custom", "Custom file…"), ("off", "Off"),
    };

    private static readonly List<MediaPlayer> Playing = new();
    private static readonly Dictionary<string, DateTime> LastPlay = new();

    private static string PathFor(string name) => Path.Combine(Paths.SoundsDir, name + "-v1.wav");

    public static void EnsureBuiltins()
    {
        try
        {
            Directory.CreateDirectory(Paths.SoundsDir);
            foreach (var (name, notes) in Defs)
            {
                var p = PathFor(name);
                if (!File.Exists(p)) File.WriteAllBytes(p, Synth(notes));
            }
        }
        catch (Exception ex) { Log.Write("Sound synth failed: " + ex.Message); }
    }

    /// <param name="channel">Debounce key: several finishes within a second play one sound.</param>
    public static void Play(string choice, string? customFile, double volume, string? channel = null)
    {
        if (choice == "off") return;
        if (channel != null)
        {
            if (LastPlay.TryGetValue(channel, out var t) && (DateTime.UtcNow - t).TotalMilliseconds < 1000) return;
            LastPlay[channel] = DateTime.UtcNow;
        }
        string path = choice == "custom" && !string.IsNullOrEmpty(customFile) && File.Exists(customFile)
            ? customFile
            : PathFor(Defs.ContainsKey(choice) ? choice : "chime");
        if (!File.Exists(path)) return;

        var player = new MediaPlayer { Volume = Math.Clamp(volume, 0, 1) };
        Playing.Add(player);
        void Done(object? s, EventArgs e) { player.Close(); Playing.Remove(player); }
        player.MediaEnded += Done;
        player.MediaFailed += (s, e) => Done(s, e);
        player.Open(new Uri(path));
        player.Play();
    }

    private static byte[] Synth(Note[] notes)
    {
        const int sr = 44100;
        double total = 0;
        foreach (var n in notes) total = Math.Max(total, n.Delay + n.Length);
        int count = (int)((total + 0.05) * sr);
        var buf = new double[count];

        foreach (var n in notes)
        {
            int start = (int)(n.Delay * sr);
            int len = (int)(n.Length * sr);
            for (int i = 0; i < len && start + i < count; i++)
            {
                double t = (double)i / sr;
                double attack = Math.Min(1, t / 0.005);
                double env = attack * Math.Exp(-t * 5.5 / n.Length);
                double phase = n.Freq * t;
                double wave = n.Triangle
                    ? 2 * Math.Abs(2 * (phase - Math.Floor(phase + 0.5))) - 1
                    : Math.Sin(2 * Math.PI * phase);
                buf[start + i] += 0.32 * env * wave;
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in buf) w.Write((short)(Math.Clamp(s, -1, 1) * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
