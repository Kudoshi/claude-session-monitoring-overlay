using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeSessionMonitoringOverlay;

public enum Kind { Input, Running, Done }

public sealed class SessionView
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required Kind Kind { get; init; }
    public bool Planning { get; init; }
    public string? Reason { get; init; }
    public long Since { get; init; }
    public bool Cli { get; init; }   // terminal session (Claude Code hook), not a desktop app session
}

public sealed record Transition(string Id, Kind To);

public sealed class Snapshot
{
    public List<SessionView> Input { get; } = new();
    public List<SessionView> Running { get; } = new();
    public List<SessionView> Done { get; } = new();
    public List<Transition> Transitions { get; } = new();
}

/// <summary>
/// Combines two sources:
///  * the desktop app's per-session files (title, archived, plan mode, last time you opened it), read-only;
///  * the status files our Claude Code hook writes (running / needs input / done), which are exact.
/// Sessions with no hook status yet fall back to a timestamp heuristic.
/// </summary>
public sealed class SessionMonitor
{
    private sealed class Desk
    {
        public string Id = "", Cli = "", Cli2 = "", Title = "", Mode = "";
        public bool Archived;
        public long Focus, Edge, UserFrame, Activity;
    }

    internal sealed class HookStatus
    {
        public string State { get; set; } = "";
        public string? Reason { get; set; }
        public long At { get; set; }
        public long StartedAt { get; set; }
        public string? Pm { get; set; }
        public string? Cwd { get; set; }
        public string? Title { get; set; }
    }

    private static readonly Regex IdRx = new("^local_[A-Za-z0-9-]{1,64}$", RegexOptions.Compiled);
    private const long Minute = 60_000;
    private const long DoneWindow = 24 * 60 * Minute;   // ignore "unseen" sessions older than a day
    private const long RunningStale = 3 * 60 * Minute;  // a "running" status this old with no activity is a crash leftover

    private readonly Dictionary<string, (DateTime stamp, Desk? desk)> _deskCache = new();
    private readonly Dictionary<string, (DateTime stamp, HookStatus? hook)> _hookCache = new();
    private readonly Dictionary<string, Kind> _prev = new();
    private readonly Dictionary<string, long> _seenLocal = new();
    private bool _first = true;
    private DateTime _lastPrune = DateTime.MinValue;

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool HookSeen { get; private set; }

    public void MarkSeen(string id) => _seenLocal[id] = NowMs();

    /// <param name="desktopHooks">Use hook status for desktop sessions (Claude Desktop hook switch).</param>
    /// <param name="cliSessions">Also show terminal sessions known only from hook status (Claude Code hook switch).</param>
    public Snapshot Poll(bool desktopHooks = true, bool cliSessions = false)
    {
        long now = NowMs();
        var desks = LoadDesks();
        var hooks = LoadHooks();
        HookSeen = hooks.Count > 0;
        bool claudeUp = IsClaudeRunning();

        var snap = new Snapshot();
        var cur = new Dictionary<string, Kind>();
        var desktopCliIds = new HashSet<string>();
        foreach (var d in desks)
        {
            if (d.Cli.Length > 0) desktopCliIds.Add(d.Cli);
            if (d.Cli2.Length > 0) desktopCliIds.Add(d.Cli2);
        }

        foreach (var d in desks)
        {
            if (d.Archived || !IdRx.IsMatch(d.Id)) continue;

            HookStatus? h1 = null, h2 = null;
            if (desktopHooks)
            {
                hooks.TryGetValue(d.Cli, out h1);
                if (d.Cli2.Length > 0 && d.Cli2 != d.Cli) hooks.TryGetValue(d.Cli2, out h2);
            }
            var h = h1 == null ? h2 : h2 == null ? h1 : (h1.At >= h2.At ? h1 : h2);

            Kind? kind = null;
            long since = 0;
            string? reason = null;
            bool planning = d.Mode == "plan";

            if (h != null && h.At >= d.UserFrame - 5000)
            {
                if (h.Pm == "plan") planning = true;
                switch (h.State)
                {
                    case "running":
                        if (claudeUp && now - Math.Max(h.At, d.Activity) < RunningStale)
                        {
                            kind = Kind.Running;
                            since = h.StartedAt > 0 ? h.StartedAt : h.At;
                        }
                        break;
                    case "input":
                        if (claudeUp) { kind = Kind.Input; since = h.At; reason = h.Reason; }
                        break;
                    case "done":
                        kind = Kind.Done; since = h.At;
                        break;
                }
            }
            else
            {
                // No hook status for this turn. The app stamps lastTurnEdgeAt at both the start and the
                // end of a turn, so "edge right next to the user's message" means the turn is still going.
                if (d.UserFrame > 0 && Math.Abs(d.Edge - d.UserFrame) < 1500 && claudeUp && now - d.Activity < 10 * Minute)
                {
                    kind = Kind.Running; since = d.UserFrame;
                }
                else if (d.Edge > d.UserFrame + 1500)
                {
                    kind = Kind.Done; since = d.Edge;
                }
            }

            if (kind == null) continue;
            Track(snap, cur, d.Id, kind.Value, since, d.Focus, DoneWindow, View(d, kind.Value, planning, reason, since), now);
        }

        // Terminal sessions: hook status files that don't belong to any desktop session.
        if (cliSessions)
        {
            foreach (var (sid, h) in hooks)
            {
                if (desktopCliIds.Contains(sid)) continue;
                Kind? kind = h.State switch
                {
                    "running" when claudeUp && now - h.At < RunningStale => Kind.Running,
                    "input" when claudeUp => Kind.Input,
                    "done" => Kind.Done,
                    _ => null,
                };
                if (kind == null) continue;
                long since = kind == Kind.Running && h.StartedAt > 0 ? h.StartedAt : h.At;
                string folder = string.IsNullOrEmpty(h.Cwd) ? "" : Path.GetFileName(h.Cwd.TrimEnd('\\', '/'));
                string title = (folder, h.Title) switch
                {
                    ("", null) => "Terminal session",
                    ("", var t) => t!,
                    (var f, null) => f,
                    (var f, var t) => $"{f} · {t}",
                };
                var v = new SessionView
                {
                    Id = "cli:" + sid, Title = title, Kind = kind.Value, Planning = h.Pm == "plan",
                    Reason = h.Reason, Since = since, Cli = true,
                };
                // Terminal sessions have no "opened" signal, so done ones clear after 2 h or when clicked.
                Track(snap, cur, v.Id, kind.Value, since, 0, 2 * 60 * Minute, v, now);
            }
        }

        _prev.Clear();
        foreach (var kv in cur) _prev[kv.Key] = kv.Value;
        _first = false;

        snap.Input.Sort((a, b) => a.Since.CompareTo(b.Since));    // waited longest first
        snap.Running.Sort((a, b) => b.Since.CompareTo(a.Since));  // newest first
        snap.Done.Sort((a, b) => b.Since.CompareTo(a.Since));     // newest first
        return snap;
    }

    private void Track(Snapshot snap, Dictionary<string, Kind> cur, string id, Kind kind, long since,
                       long focus, long doneWindow, SessionView view, long now)
    {
        cur[id] = kind;
        bool hadPrev = _prev.TryGetValue(id, out var prevKind);

        if (kind != Kind.Done && hadPrev && prevKind == Kind.Done)
            _seenLocal.Remove(id); // new turn started

        if (kind == Kind.Done)
        {
            bool seen = focus > since || (_seenLocal.TryGetValue(id, out var s) && s >= since);
            if (!_first && hadPrev && prevKind != Kind.Done)
                snap.Transitions.Add(new Transition(id, Kind.Done));
            if (!seen && now - since < doneWindow)
                snap.Done.Add(view);
        }
        else
        {
            if (!_first && kind == Kind.Input && (!hadPrev || prevKind != Kind.Input))
                snap.Transitions.Add(new Transition(id, Kind.Input));
            (kind == Kind.Input ? snap.Input : snap.Running).Add(view);
        }
    }

    private static SessionView View(Desk d, Kind k, bool planning, string? reason, long since) => new()
    {
        Id = d.Id,
        Title = string.IsNullOrWhiteSpace(d.Title) ? "Untitled session" : d.Title.Trim(),
        Kind = k,
        Planning = planning,
        Reason = reason,
        Since = since,
    };

    private List<Desk> LoadDesks()
    {
        var list = new List<Desk>();
        if (!Directory.Exists(Paths.SessionsRoot)) return list;
        var alive = new HashSet<string>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(Paths.SessionsRoot, "local_*.json", SearchOption.AllDirectories).ToList(); }
        catch { return list; }

        foreach (var f in files)
        {
            alive.Add(f);
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(f); } catch { continue; }
            if (_deskCache.TryGetValue(f, out var c) && c.stamp == stamp)
            {
                if (c.desk != null) list.Add(c.desk);
                continue;
            }
            var desk = ReadDesk(f);
            if (desk == null && c.desk != null) { list.Add(c.desk); continue; } // mid-write; keep last good
            _deskCache[f] = (stamp, desk);
            if (desk != null) list.Add(desk);
        }
        foreach (var gone in _deskCache.Keys.Where(k => !alive.Contains(k)).ToList()) _deskCache.Remove(gone);
        return list;
    }

    private static Desk? ReadDesk(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var r = doc.RootElement;
            return new Desk
            {
                Id = Str(r, "sessionId"),
                Cli = Str(r, "cliSessionId"),
                Cli2 = Str(r, "lastAssistantCliSessionId"),
                Title = Str(r, "title"),
                Mode = Str(r, "permissionMode"),
                Archived = r.TryGetProperty("isArchived", out var a) && a.ValueKind == JsonValueKind.True,
                Focus = Num(r, "lastFocusedAt"),
                Edge = Num(r, "lastTurnEdgeAt"),
                UserFrame = Num(r, "latestUserFrameAt"),
                Activity = Num(r, "lastActivityAt"),
            };
        }
        catch { return null; }
    }

    private Dictionary<string, HookStatus> LoadHooks()
    {
        var map = new Dictionary<string, HookStatus>();
        if (!Directory.Exists(Paths.State)) return map;
        bool prune = (DateTime.UtcNow - _lastPrune).TotalHours > 1;
        if (prune) _lastPrune = DateTime.UtcNow;

        foreach (var f in Directory.EnumerateFiles(Paths.State, "*.json"))
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(f); } catch { continue; }
            if (prune && (DateTime.UtcNow - stamp).TotalDays > 7)
            {
                try { File.Delete(f); } catch { }
                continue;
            }
            string sid = Path.GetFileNameWithoutExtension(f);
            if (!_hookCache.TryGetValue(f, out var c) || c.stamp != stamp)
            {
                var h = HookWriter.ReadStatus(f);
                if (h == null && c.hook != null) h = c.hook;
                c = (stamp, h);
                _hookCache[f] = c;
            }
            if (c.hook != null) map[sid] = c.hook;
        }
        return map;
    }

    private static bool IsClaudeRunning()
    {
        try
        {
            var ps = Process.GetProcessesByName("claude");
            bool up = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            return up;
        }
        catch { return true; }
    }

    private static string Str(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
