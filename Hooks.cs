using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSessionMonitoringOverlay;

/// <summary>Runs inside a Claude Code hook. Reads the event JSON from stdin and writes one small status file per session.</summary>
internal static class HookWriter
{

    public static int Run()
    {
        try
        {
            string input = Console.In.ReadToEnd();
            using var doc = JsonDocument.Parse(input);
            var r = doc.RootElement;
            string sid = Str(r, "session_id");
            if (sid.Length == 0 || sid.Length > 100 || sid.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return 0;

            string ev = Str(r, "hook_event_name");
            string tool = Str(r, "tool_name");
            string ntype = Str(r, "notification_type");
            string pm = Str(r, "permission_mode");
            string cwd = Str(r, "cwd");
            string prompt = Str(r, "prompt");

            string? state = null, reason = null;
            switch (ev)
            {
                case "UserPromptSubmit":
                    state = "running"; break;
                case "PreToolUse":
                    if (tool == "AskUserQuestion") { state = "input"; reason = "asked a question"; }
                    else if (tool == "ExitPlanMode") { state = "input"; reason = "plan ready to approve"; }
                    else state = "running";
                    break;
                case "PostToolUse":
                case "PostToolUseFailure":
                    state = "running"; break;
                case "PermissionRequest":
                    state = "input"; reason = "permission: " + ShortTool(tool); break;
                case "Notification":
                    if (ntype == "permission_prompt") { state = "input"; reason = "needs permission"; }
                    else if (ntype is "elicitation_dialog" or "elicitation_url_dialog") { state = "input"; reason = "asked a question"; }
                    else if (ntype == "agent_needs_input") { state = "input"; reason = "waiting for you"; }
                    break;
                case "Stop":
                    state = "done"; break;
                case "SessionEnd":
                    state = "ended"; break;
            }
            if (state == null) return 0;

            Directory.CreateDirectory(Paths.State);
            string file = Path.Combine(Paths.State, sid + ".json");
            var prev = File.Exists(file) ? ReadStatus(file) : null;
            long now = SessionMonitor.NowMs();

            // Keep the more specific reason from PermissionRequest/PreToolUse over the generic notification.
            if (ev == "Notification" && prev?.State == "input") return 0;
            // A session closing after it finished stays "done" (unseen tracking keeps working).
            if (state == "ended" && prev?.State == "done") return 0;
            // Tool hooks run async, so the last tool's hook can land just after Stop. Don't let it undo "done".
            if (ev is "PreToolUse" or "PostToolUse" or "PostToolUseFailure" && prev?.State == "done" && now - prev.At < 3000) return 0;

            var status = new SessionMonitor.HookStatus
            {
                State = state,
                Reason = reason,
                At = now,
                StartedAt = state == "running" && prev is { State: "running" or "input" } ? prev.StartedAt : now,
                Pm = pm.Length > 0 ? pm : null,
                Cwd = cwd.Length > 0 ? cwd : prev?.Cwd,
                Title = prompt.Length > 0 ? Snippet(prompt) : prev?.Title,
            };
            if (state == "input" && prev is { State: "running" or "input" }) status.StartedAt = prev.StartedAt;

            string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartObject();
                w.WriteString("state", status.State);
                if (status.Reason != null) w.WriteString("reason", status.Reason);
                w.WriteNumber("at", status.At);
                w.WriteNumber("startedAt", status.StartedAt);
                if (status.Pm != null) w.WriteString("pm", status.Pm);
                if (status.Cwd != null) w.WriteString("cwd", status.Cwd);
                if (status.Title != null) w.WriteString("title", status.Title);
                w.WriteEndObject();
            }
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            // Never break Claude because of the overlay.
        }
        return 0;
    }

    internal static SessionMonitor.HookStatus? ReadStatus(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var r = doc.RootElement;
            string reason = Str(r, "reason"), pm = Str(r, "pm"), cwd = Str(r, "cwd"), title = Str(r, "title");
            return new SessionMonitor.HookStatus
            {
                State = Str(r, "state"),
                Reason = reason.Length > 0 ? reason : null,
                At = r.TryGetProperty("at", out var a) && a.TryGetInt64(out var at) ? at : 0,
                StartedAt = r.TryGetProperty("startedAt", out var b) && b.TryGetInt64(out var st) ? st : 0,
                Pm = pm.Length > 0 ? pm : null,
                Cwd = cwd.Length > 0 ? cwd : null,
                Title = title.Length > 0 ? title : null,
            };
        }
        catch { return null; }
    }

    // First line of the prompt, trimmed, as a session title for terminal sessions.
    private static string Snippet(string prompt)
    {
        var line = prompt.Trim().Split('\n')[0].Trim();
        return line.Length > 70 ? line[..70].TrimEnd() + "…" : line;
    }

    private static string ShortTool(string tool)
    {
        if (tool.Length == 0) return "tool";
        if (tool.StartsWith("mcp__"))
        {
            var parts = tool.Split("__");
            return parts[^1];
        }
        return tool;
    }

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>
/// Two user-facing switches (Claude Desktop sessions, Claude Code terminal sessions) over one shared set of
/// hook entries. The entries stay installed while either switch is on.
/// </summary>
internal static class HookSwitches
{
    /// <summary>First run after upgrading: an already-installed hook means the Desktop switch is on.</summary>
    public static void Migrate(AppSettings s)
    {
        if (s.TrackDesktop != null && s.TrackCli != null) return;
        bool installed = HookInstaller.IsInstalled();
        s.TrackDesktop ??= installed;
        s.TrackCli ??= false;
        s.Save();
    }

    public static bool IsOn(AppSettings s, bool desktop) =>
        (desktop ? s.TrackDesktop : s.TrackCli) == true;

    /// <summary>Runs at startup. If a switch is on but the entries are missing or point at another copy of the app
    /// (after the rename or a move), re-point them at this exe.</summary>
    public static void Reconcile(AppSettings s, string exePath)
    {
        if (s.TrackDesktop != true && s.TrackCli != true) return;
        if (!HookInstaller.PointsTo(exePath)) HookInstaller.Install(exePath);
    }

    public static bool Set(AppSettings s, bool desktop, bool on)
    {
        bool otherOn = (desktop ? s.TrackCli : s.TrackDesktop) == true;
        bool ok = true;
        if (on && !HookInstaller.IsInstalled()) ok = HookInstaller.Install(Environment.ProcessPath!);
        if (!on && !otherOn) ok = HookInstaller.Uninstall();
        if (ok)
        {
            if (desktop) s.TrackDesktop = on; else s.TrackCli = on;
            s.Save();
        }
        return ok;
    }
}

/// <summary>Adds or removes the overlay's hook entries in ~/.claude/settings.json, leaving everything else untouched.</summary>
internal static class HookInstaller
{
    // Tool hooks fire on every tool call, so they run async and never slow Claude down.
    // Once-per-turn events stay synchronous so their order is guaranteed.
    private static readonly (string ev, string? matcher, bool async)[] Events =
    {
        ("UserPromptSubmit", null, false),
        ("PreToolUse", "*", true),
        ("PostToolUse", "*", true),
        ("PostToolUseFailure", "*", true),
        ("PermissionRequest", "*", false),
        ("Notification", null, true),
        ("Stop", null, false),
        ("SessionEnd", null, true),
    };

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string CommandFor(string exePath) => "\"" + exePath.Replace('\\', '/') + "\" --hook";

    private static bool IsOurs(JsonNode? hookEntry)
    {
        var cmd = hookEntry?["command"]?.GetValue<string>() ?? "";
        bool ours = cmd.Contains("ClaudeSessionMonitoringOverlay.exe", StringComparison.OrdinalIgnoreCase) || cmd.Contains("ClaudeOverlay.exe", StringComparison.OrdinalIgnoreCase);
        return ours && cmd.Contains("--hook");
    }

    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(Paths.ClaudeSettings)) return false;
            var root = JsonNode.Parse(File.ReadAllText(Paths.ClaudeSettings)) as JsonObject;
            if (root?["hooks"] is not JsonObject hooks) return false;
            return hooks.Any(kv => kv.Value is JsonArray arr &&
                arr.Any(m => m?["hooks"] is JsonArray hs && hs.Any(IsOurs)));
        }
        catch { return false; }
    }

    /// <summary>True when our hook entries exist and all of them run this exact exe (not an old copy).</summary>
    public static bool PointsTo(string exePath)
    {
        try
        {
            if (!File.Exists(Paths.ClaudeSettings)) return false;
            var root = JsonNode.Parse(File.ReadAllText(Paths.ClaudeSettings)) as JsonObject;
            if (root?["hooks"] is not JsonObject hooks) return false;
            string want = CommandFor(exePath);
            bool any = false;
            foreach (var kv in hooks)
            {
                if (kv.Value is not JsonArray arr) continue;
                foreach (var m in arr)
                    if (m?["hooks"] is JsonArray hs)
                        foreach (var h in hs)
                            if (IsOurs(h))
                            {
                                any = true;
                                if (h?["command"]?.GetValue<string>() != want) return false;
                            }
            }
            return any;
        }
        catch { return false; }
    }

    public static bool Install(string exePath)
    {
        try
        {
            var root = Load(out bool existed);
            if (existed) File.Copy(Paths.ClaudeSettings, Paths.ClaudeSettings + ".bak-overlay", overwrite: true);
            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            root["hooks"] = hooks;
            RemoveOurs(hooks);

            string cmd = CommandFor(exePath);
            foreach (var (ev, matcher, async) in Events)
            {
                var arr = hooks[ev] as JsonArray ?? new JsonArray();
                hooks[ev] = arr;
                var entry = new JsonObject();
                if (matcher != null) entry["matcher"] = matcher;
                var hook = new JsonObject { ["type"] = "command", ["command"] = cmd };
                if (async) hook["async"] = true; else hook["timeout"] = 5;
                entry["hooks"] = new JsonArray(hook);
                arr.Add(entry);
            }
            Save(root);
            return true;
        }
        catch (Exception ex) { Log.Write("Install hook failed: " + ex.Message); return false; }
    }

    public static bool Uninstall()
    {
        try
        {
            if (!File.Exists(Paths.ClaudeSettings)) return true;
            var root = Load(out _);
            if (root["hooks"] is JsonObject hooks)
            {
                RemoveOurs(hooks);
                if (hooks.Count == 0) root.Remove("hooks");
            }
            Save(root);
            return true;
        }
        catch (Exception ex) { Log.Write("Uninstall hook failed: " + ex.Message); return false; }
    }

    private static void RemoveOurs(JsonObject hooks)
    {
        foreach (var key in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[key] is not JsonArray arr) continue;
            for (int i = arr.Count - 1; i >= 0; i--)
            {
                if (arr[i]?["hooks"] is not JsonArray hs) continue;
                for (int j = hs.Count - 1; j >= 0; j--)
                    if (IsOurs(hs[j])) hs.RemoveAt(j);
                if (hs.Count == 0) arr.RemoveAt(i);
            }
            if (arr.Count == 0) hooks.Remove(key);
        }
    }

    private static JsonObject Load(out bool existed)
    {
        existed = File.Exists(Paths.ClaudeSettings);
        if (!existed) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(Paths.ClaudeSettings)) as JsonObject ?? new JsonObject();
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.ClaudeSettings)!);
        string tmp = Paths.ClaudeSettings + ".tmp-overlay";
        File.WriteAllText(tmp, root.ToJsonString(WriteOpts));
        File.Move(tmp, Paths.ClaudeSettings, overwrite: true);
    }
}
