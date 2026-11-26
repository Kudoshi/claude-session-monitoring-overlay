using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClaudeOverlay;

public partial class OverlayWindow : Window
{
    private const int MaxRows = 6;
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    private static readonly Color AmberFlash = (Color)ColorConverter.ConvertFromString("#854F0B");
    private static readonly Color GreenFlash = (Color)ColorConverter.ConvertFromString("#3B6D11");

    private readonly AppSettings _s;
    private readonly SessionMonitor _monitor = new();
    private readonly Dictionary<string, RowUi> _rows = new();
    private readonly Dictionary<Panel, (TextBlock more, TextBlock empty)> _extras = new();
    private readonly Native.LowLevelKeyboardProc _kbProc;

    private IntPtr _hwnd, _kbHook;
    private bool _live, _dragging, _pulsing, _previewing;
    private int _lastInput = -1, _lastRunning = -1, _lastDone = -1;
    private int _hookCheckCountdown;
    private bool _hookInstalled = true;
    private DispatcherTimer? _keyTimer, _pollTimer, _hintTimer;
    private System.Windows.Forms.NotifyIcon? _tray;
    private SettingsWindow? _settingsWin;

    private sealed class RowUi
    {
        public required string Id;
        public required Kind Kind;
        public required Border Root;
        public required TextBlock Icon, Title, BadgeText, Detail;
        public required Border Badge;
        public bool? Planning;
        public bool IsNew = true;
    }

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        _s = settings;
        _kbProc = KeyboardHook;
        Opacity = _s.Opacity;
        PlaceInitially();
        SourceInitialized += OnSourceInitialized;
        Closed += (_, _) => Cleanup();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (_s.Theme == "system") Dispatcher.Invoke(() => Theme.Apply(_s));
        };
    }

    // ---------- window setup ----------

    private void PlaceInitially()
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                          SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (_s.Left is double l && _s.Top is double t && vs.Contains(new Point(l + 40, t + 20)))
        {
            Left = l; Top = t;
        }
        else ResetPosition();
    }

    private void ResetPosition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Top + 16;
        SavePosition();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

        _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, Native.GetModuleHandle(null), 0);
        if (_kbHook == IntPtr.Zero) Log.Write("Keyboard hook failed: " + Marshal.GetLastWin32Error());

        _keyTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => CheckCtrl(), Dispatcher);
        _pollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _keyTimer.Start();
        _pollTimer.Start();

        SetupTray();
        Refresh();
    }

    private void SetClickThrough(bool on)
    {
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~(long)Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    // ---------- Ctrl mode ----------

    private void CheckCtrl()
    {
        bool want = Native.CtrlDown() || _dragging;
        if (want == _live) return;
        _live = want;
        SetClickThrough(!_live);

        _previewing = false;
        FadeTo(_live ? _s.CtrlOpacity : _s.Opacity);

        var dur = TimeSpan.FromMilliseconds(160);
        LiveOutline.BeginAnimation(OpacityProperty, new DoubleAnimation(_live ? 1 : 0, dur));
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(_live ? 1.01 : 1, dur));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(_live ? 1.01 : 1, dur));
        GearButton.BeginAnimation(OpacityProperty, new DoubleAnimation(_live ? 1 : 0.35, dur));
        DragBar.Cursor = _live ? Cursors.SizeAll : null;
        if (_hintTimer?.IsEnabled != true)
            Hint.Text = _live ? "Drag to move · 1–5 opacity" : "Hold Ctrl to interact";
        if (!_live)
            foreach (var r in _rows.Values) Nudge(r.Root, 0);
    }

    private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_live || e.OriginalSource is DependencyObject d && IsInside(d, GearButton)) return;
        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally
        {
            _dragging = false;
            SavePosition();
        }
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
            if (cur == ancestor) return true;
        return false;
    }

    private void SavePosition()
    {
        _s.Left = Left; _s.Top = Top;
        _s.Save();
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)Native.WM_KEYDOWN || wParam == (IntPtr)Native.WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);
            int n = vk is >= 0x31 and <= 0x35 ? vk - 0x30 : vk is >= 0x61 and <= 0x65 ? vk - 0x60 : 0;
            if (n > 0 && Native.CtrlDown() && CursorOverMe())
            {
                Dispatcher.BeginInvoke(() => SetOpacityLevel(n));
                return (IntPtr)1; // swallow only here; Ctrl+1..5 elsewhere is untouched
            }
        }
        return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private bool CursorOverMe()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetCursorPos(out var p) || !Native.GetWindowRect(_hwnd, out var r)) return false;
        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    private void SetOpacityLevel(int n)
    {
        SetOpacity(n * 0.2);
        // Preview the new level while Ctrl is still held, instead of the Ctrl opacity.
        _previewing = true;
        FadeTo(_s.Opacity);
        ShowHint($"Opacity {n * 20}%");
        _settingsWin?.SyncFromSettings();
    }

    public void SetOpacity(double v)
    {
        _s.Opacity = Math.Clamp(v, 0.2, 1);
        if (!_live) FadeTo(_s.Opacity);
        _s.Save();
    }

    public void SetCtrlOpacity(double v)
    {
        _s.CtrlOpacity = Math.Clamp(v, 0.2, 1);
        if (_live && !_previewing) FadeTo(_s.CtrlOpacity);
        _s.Save();
    }

    private void FadeTo(double target) =>
        BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(160)));

    private void ShowHint(string text)
    {
        Hint.Text = text;
        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Normal, (_, _) =>
        {
            _hintTimer!.Stop();
            Hint.Text = _live ? "Drag to move · 1–5 opacity" : "Hold Ctrl to interact";
        }, Dispatcher);
        _hintTimer.Start();
    }

    // ---------- settings / tray ----------

    private void GearButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_live) return;
        var rt = new DoubleAnimation(0, 90, TimeSpan.FromMilliseconds(350)) { EasingFunction = new QuadraticEase() };
        GearRotate.BeginAnimation(RotateTransform.AngleProperty, rt);
        OpenSettings();
    }

    internal void OpenSettings()
    {
        if (_settingsWin == null || !_settingsWin.IsLoaded)
        {
            _settingsWin = new SettingsWindow(_s, this);
            _settingsWin.Closed += (_, _) => _settingsWin = null;
            _settingsWin.Show();
        }
        _settingsWin.Activate();
    }

    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "Claude overlay",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        _tray.ContextMenuStrip.Items.Add("Reset position", null, (_, _) => Dispatcher.Invoke(ResetPosition));
        _tray.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(() => { Close(); Application.Current.Shutdown(); }));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(OpenSettings);
    }

    private static System.Drawing.Icon MakeTrayIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        using var stream = info!.Stream;
        return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private void Cleanup()
    {
        _keyTimer?.Stop();
        _pollTimer?.Stop();
        if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook);
        _kbHook = IntPtr.Zero;
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }

    // ---------- data → UI ----------

    private void Refresh()
    {
        Snapshot snap;
        try { snap = _monitor.Poll(_s.TrackDesktop == true, _s.TrackCli == true); }
        catch (Exception ex) { Log.Write("Poll failed: " + ex); return; }

        var flash = new HashSet<string>();
        bool playDone = false, playInput = false;
        foreach (var t in snap.Transitions)
        {
            flash.Add(Key(t.To, t.Id));
            if (t.To == Kind.Done) playDone = true;
            if (t.To == Kind.Input) playInput = true;
        }
        if (playInput) Sounds.Play(_s.InputSound, _s.InputSoundFile, _s.Volume, "input");
        if (playDone) Sounds.Play(_s.DoneSound, _s.DoneSoundFile, _s.Volume, "done");

        UpdateCount(InputCount, InputHeaderCount, snap.Input.Count, ref _lastInput);
        UpdateCount(RunningCount, RunningHeaderCount, snap.Running.Count, ref _lastRunning);
        UpdateCount(DoneCount, DoneHeaderCount, snap.Done.Count, ref _lastDone);

        if (_hookCheckCountdown-- <= 0)
        {
            _hookCheckCountdown = 4; // re-read settings.json every ~6 s
            _hookInstalled = HookSwitches.IsOn(_s, desktop: true);
            HookBanner.Visibility = !_hookInstalled && !_s.HookPromptDismissed ? Visibility.Visible : Visibility.Collapsed;
        }

        var live = new HashSet<string>();
        SyncList(InputList, Kind.Input, snap.Input, flash, live,
                 _hookInstalled ? "Nothing needs input" : "Needs the Claude Desktop hook (Settings)");
        SyncList(RunningList, Kind.Running, snap.Running, flash, live, "Nothing running");
        SyncList(DoneList, Kind.Done, snap.Done, flash, live, "All caught up");
        foreach (var k in _rows.Keys.Where(k => !live.Contains(k)).ToList()) _rows.Remove(k);

        SetPulse(snap.Input.Count > 0);
    }

    private static string Key(Kind k, string id) => k + ":" + id;

    // ---------- hook setup banner ----------

    private void HookBanner_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_live) return;
        var answer = MessageBox.Show(
            "Install the Claude Desktop hook?\n\n" +
            "This adds a few entries to ~/.claude/settings.json (a backup is saved first). " +
            "Claude Code then tells the overlay when a session starts, needs your input, or finishes. " +
            "Tool events run in the background, so Claude isn't slowed down.\n\n" +
            "Sessions already in progress are picked up from their next turn. " +
            "You can remove it any time in Settings.",
            "Claude overlay", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        if (HookSwitches.Set(_s, desktop: true, on: true))
        {
            ShowHint("Hook installed");
            _hookCheckCountdown = 0;
            Refresh();
        }
        else ShowHint("Couldn't install the hook");
    }

    private void HookBannerClose_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_live) return;
        _s.HookPromptDismissed = true;
        _s.Save();
        HookBanner.Visibility = Visibility.Collapsed;
    }

    private static void UpdateCount(TextBlock tile, TextBlock header, int value, ref int last)
    {
        header.Text = value.ToString();
        if (value == last) return;
        bool bump = last >= 0;
        last = value;
        tile.Text = value.ToString();
        if (!bump) return;
        var st = (ScaleTransform)tile.RenderTransform;
        var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(450) };
        k.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromPercent(0.4), new QuadraticEase()));
        k.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new QuadraticEase()));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, k);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, k);
    }

    private void SyncList(Panel panel, Kind kind, List<SessionView> items, HashSet<string> flash,
                          HashSet<string> live, string emptyText)
    {
        if (!_extras.TryGetValue(panel, out var extra))
        {
            extra = (MutedLine(""), MutedLine(emptyText));
            _extras[panel] = extra;
        }
        if (extra.empty.Text != emptyText) extra.empty.Text = emptyText;

        var desired = new List<UIElement>();
        long now = SessionMonitor.NowMs();
        foreach (var v in items.Take(MaxRows))
        {
            string key = Key(kind, v.Id);
            live.Add(key);
            if (!_rows.TryGetValue(key, out var row))
            {
                row = MakeRow(v.Id, kind);
                _rows[key] = row;
            }
            UpdateRow(row, v, now);
            desired.Add(row.Root);
        }
        if (items.Count > MaxRows)
        {
            extra.more.Text = $"+{items.Count - MaxRows} more";
            desired.Add(extra.more);
        }
        if (items.Count == 0) desired.Add(extra.empty);

        if (!panel.Children.Cast<UIElement>().SequenceEqual(desired))
        {
            panel.Children.Clear();
            foreach (var el in desired) panel.Children.Add(el);
        }

        foreach (var v in items.Take(MaxRows))
        {
            var row = _rows[Key(kind, v.Id)];
            if (!row.IsNew) continue;
            row.IsNew = false;
            AnimateIn(row.Root);
            if (flash.Contains(Key(kind, v.Id)))
                Flash(row.Root, kind == Kind.Input ? AmberFlash : GreenFlash);
        }
    }

    private static TextBlock MutedLine(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(2, 4, 0, 4) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return tb;
    }

    private RowUi MakeRow(string id, Kind kind)
    {
        var icon = new TextBlock { FontFamily = IconFont, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        var title = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        var badgeText = new TextBlock { FontSize = 10.5 };
        var badge = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = badgeText,
        };
        var detail = new TextBlock { FontSize = 11, FontFamily = MonoFont, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(icon, 0); Grid.SetColumn(title, 1); Grid.SetColumn(badge, 2); Grid.SetColumn(detail, 3);
        g.Children.Add(icon); g.Children.Add(title); g.Children.Add(badge); g.Children.Add(detail);

        var root = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 3, 0, 3),
            Child = g, RenderTransform = new TranslateTransform(),
        };
        root.SetResourceReference(Border.BackgroundProperty, "RowBg");

        var row = new RowUi { Id = id, Kind = kind, Root = root, Icon = icon, Title = title, Badge = badge, BadgeText = badgeText, Detail = detail };
        root.MouseEnter += (_, _) => { if (_live) { Nudge(root, 3); root.Cursor = Cursors.Hand; } };
        root.MouseLeave += (_, _) => { Nudge(root, 0); root.Cursor = null; };
        root.MouseLeftButtonUp += (_, _) => { if (_live) OpenSession(row); };
        return row;
    }

    private static void UpdateRow(RowUi row, SessionView v, long now)
    {
        if (row.Title.Text != v.Title) { row.Title.Text = v.Title; row.Root.ToolTip = v.Title; }
        switch (row.Kind)
        {
            case Kind.Input:
                row.Icon.Text = "";
                row.Icon.SetResourceReference(TextBlock.ForegroundProperty, "Amber");
                row.BadgeText.Text = v.Reason ?? "waiting for you";
                if (row.Planning == null)
                {
                    row.Badge.SetResourceReference(Border.BackgroundProperty, "AmBg");
                    row.BadgeText.SetResourceReference(TextBlock.ForegroundProperty, "AmFg");
                    row.Planning = false;
                }
                row.Detail.Text = Ago(now - v.Since, false);
                break;
            case Kind.Running:
                if (row.Planning != v.Planning)
                {
                    row.Planning = v.Planning;
                    row.Icon.Text = v.Planning ? "" : "";
                    row.Icon.SetResourceReference(TextBlock.ForegroundProperty, v.Planning ? "Purple" : "Blue");
                    row.BadgeText.Text = v.Planning ? "planning" : "executing";
                    row.Badge.SetResourceReference(Border.BackgroundProperty, v.Planning ? "PlBg" : "ExBg");
                    row.BadgeText.SetResourceReference(TextBlock.ForegroundProperty, v.Planning ? "PlFg" : "ExFg");
                }
                row.Detail.Text = Ago(now - v.Since, false);
                break;
            case Kind.Done:
                row.Icon.Text = "";
                row.Icon.SetResourceReference(TextBlock.ForegroundProperty, "Green");
                row.Badge.Visibility = Visibility.Collapsed;
                row.Detail.Text = Ago(now - v.Since, true);
                break;
        }
        if (v.Cli) row.Detail.Text += " · CLI";
    }

    private static string Ago(long ms, bool suffix)
    {
        long s = Math.Max(0, ms / 1000);
        string t = s < 60 ? (suffix ? "just now" : "<1m")
            : s < 3600 ? $"{s / 60}m"
            : s < 86400 ? $"{s / 3600}h"
            : $"{s / 86400}d";
        return suffix && s >= 60 ? t + " ago" : t;
    }

    // ---------- open in Claude ----------

    private void OpenSession(RowUi row)
    {
        if (row.Id.StartsWith("cli:"))
        {
            // Terminal sessions can't be opened in the desktop app; clicking a done one just clears it.
            ShowHint("Terminal session: switch to your terminal");
            if (row.Kind == Kind.Done) { _monitor.MarkSeen(row.Id); Refresh(); }
            return;
        }
        try
        {
            Native.AllowSetForegroundWindow(-1);
            Process.Start(new ProcessStartInfo("claude://code/continue?session=" + row.Id) { UseShellExecute = true });
            BringClaudeForward();
            ShowHint("Opening in Claude");
        }
        catch (Exception ex)
        {
            Log.Write("Open session failed: " + ex.Message);
            ShowHint("Couldn't open Claude");
        }
        if (row.Kind == Kind.Done)
        {
            _monitor.MarkSeen(row.Id);
            Refresh();
        }
    }

    private static void BringClaudeForward()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("claude"))
            {
                using (p)
                {
                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                    Native.SetForegroundWindow(h);
                    break;
                }
            }
        }
        catch { }
    }

    // ---------- animation helpers ----------

    private static void AnimateIn(FrameworkElement el)
    {
        var dur = TimeSpan.FromMilliseconds(350);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        if (el.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, dur) { EasingFunction = ease });
    }

    private static void Flash(Border b, Color from)
    {
        var brush = new SolidColorBrush(from);
        b.Background = brush;
        var anim = new ColorAnimation(from, Theme.RowColor, TimeSpan.FromMilliseconds(1200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "RowBg");
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private static void Nudge(Border b, double x)
    {
        if (b.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(120)));
    }

    private void SetPulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        if (!on)
        {
            InputRing.BeginAnimation(OpacityProperty, null);
            InputRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            InputRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            InputRing.Opacity = 0;
            return;
        }
        var dur = new Duration(TimeSpan.FromMilliseconds(1100));
        var scale = new DoubleAnimation(0.6, 1.6, dur) { RepeatBehavior = RepeatBehavior.Forever };
        var fade = new DoubleAnimation(0.8, 0, dur) { RepeatBehavior = RepeatBehavior.Forever };
        InputRingScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        InputRingScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        InputRing.BeginAnimation(OpacityProperty, fade);
    }
}
