using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeOverlay;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly OverlayWindow _overlay;
    private bool _loading = true;

    public SettingsWindow(AppSettings settings, OverlayWindow overlay)
    {
        InitializeComponent();
        _s = settings;
        _overlay = overlay;
        MaxHeight = SystemParameters.WorkArea.Height - 40;

        foreach (var (key, label) in Sounds.Choices)
        {
            DoneBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            InputBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        }

        SyncFromSettings();
        Select(ThemeBox, _s.Theme);
        Select(DoneBox, _s.DoneSound);
        Select(InputBox, _s.InputSound);
        VolumeSlider.Value = Math.Round(_s.Volume * 100);
        VolumeValue.Text = $"{Math.Round(_s.Volume * 100)}%";
        StartupBox.IsChecked = StartupEntry.IsEnabled();
        UpdateFileRows();
        UpdateHookUi();
        _loading = false;
    }

    public void SyncFromSettings()
    {
        bool was = _loading;
        _loading = true;
        OpacitySlider.Value = Math.Round(_s.Opacity * 100);
        OpacityValue.Text = $"{Math.Round(_s.Opacity * 100)}%";
        CtrlOpacitySlider.Value = Math.Round(_s.CtrlOpacity * 100);
        CtrlOpacityValue.Text = $"{Math.Round(_s.CtrlOpacity * 100)}%";
        BgSlider.Value = Math.Round(_s.BgOpacity * 100);
        CardSlider.Value = Math.Round(_s.CardOpacity * 100);
        TextSlider.Value = Math.Round(_s.TextOpacity * 100);
        UpdateLayerLabels();
        _loading = was;
    }

    private void UpdateLayerLabels()
    {
        if (BgValue == null || CardValue == null || TextValue == null) return;
        BgValue.Text = $"{Math.Round(BgSlider.Value)}%";
        CardValue.Text = $"{Math.Round(CardSlider.Value)}%";
        TextValue.Text = $"{Math.Round(TextSlider.Value)}%";
    }

    private void LayerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLayerLabels();
        if (_loading || BgSlider == null || CardSlider == null || TextSlider == null) return;
        _s.BgOpacity = BgSlider.Value / 100;
        _s.CardOpacity = CardSlider.Value / 100;
        _s.TextOpacity = TextSlider.Value / 100;
        Theme.Apply(_s);
        _s.Save();
    }

    private void ResetLayers_Click(object sender, RoutedEventArgs e)
    {
        _s.BgOpacity = _s.CardOpacity = _s.TextOpacity = 1;
        Theme.Apply(_s);
        _s.Save();
        SyncFromSettings();
    }

    private static void Select(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];
    }

    private static string SelTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        OpacityValue.Text = $"{Math.Round(e.NewValue)}%";
        if (!_loading) _overlay.SetOpacity(e.NewValue / 100);
    }

    private void CtrlOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CtrlOpacityValue != null) CtrlOpacityValue.Text = $"{Math.Round(e.NewValue)}%";
        if (!_loading) _overlay.SetCtrlOpacity(e.NewValue / 100);
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _s.Theme = SelTag(ThemeBox);
        Theme.Apply(_s);
        _s.Save();
    }

    private void DoneBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _s.DoneSound = SelTag(DoneBox);
        if (_s.DoneSound == "custom" && string.IsNullOrEmpty(_s.DoneSoundFile)) PickFile(true);
        _s.Save();
        UpdateFileRows();
    }

    private void InputBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _s.InputSound = SelTag(InputBox);
        if (_s.InputSound == "custom" && string.IsNullOrEmpty(_s.InputSoundFile)) PickFile(false);
        _s.Save();
        UpdateFileRows();
    }

    private void ChooseDone_Click(object sender, RoutedEventArgs e) { PickFile(true); _s.Save(); UpdateFileRows(); }
    private void ChooseInput_Click(object sender, RoutedEventArgs e) { PickFile(false); _s.Save(); UpdateFileRows(); }

    private void PickFile(bool done)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = done ? "Choose the done sound" : "Choose the needs input sound",
            Filter = "Audio files|*.wav;*.mp3;*.wma;*.m4a|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        if (done) _s.DoneSoundFile = dlg.FileName; else _s.InputSoundFile = dlg.FileName;
    }

    private void UpdateFileRows()
    {
        DoneFileRow.Visibility = _s.DoneSound == "custom" ? Visibility.Visible : Visibility.Collapsed;
        InputFileRow.Visibility = _s.InputSound == "custom" ? Visibility.Visible : Visibility.Collapsed;
        DoneFileText.Text = string.IsNullOrEmpty(_s.DoneSoundFile) ? "No file chosen" : Path.GetFileName(_s.DoneSoundFile);
        InputFileText.Text = string.IsNullOrEmpty(_s.InputSoundFile) ? "No file chosen" : Path.GetFileName(_s.InputSoundFile);
    }

    private void TestDone_Click(object sender, RoutedEventArgs e) => Sounds.Play(_s.DoneSound, _s.DoneSoundFile, _s.Volume);
    private void TestInput_Click(object sender, RoutedEventArgs e) => Sounds.Play(_s.InputSound, _s.InputSoundFile, _s.Volume);

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeValue != null) VolumeValue.Text = $"{Math.Round(e.NewValue)}%";
        if (_loading) return;
        _s.Volume = e.NewValue / 100;
        _s.Save();
    }

    private void StartupBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { StartupEntry.Set(StartupBox.IsChecked == true); }
        catch (Exception ex) { Log.Write("Startup entry failed: " + ex.Message); }
    }

    private void UpdateHookUi()
    {
        bool desktop = HookSwitches.IsOn(_s, desktop: true);
        bool code = HookSwitches.IsOn(_s, desktop: false);

        DesktopStatus.Text = desktop
            ? "Desktop app sessions report when they start, need your input, or finish."
            : "Turn on to see Needs input and exact running and done states for desktop app sessions.";
        CodeStatus.Text = code
            ? "Sessions you run with `claude` in a terminal also show on the overlay, marked CLI."
            : "Turn on to also show sessions you run with `claude` in a terminal.";

        SetCard(DesktopPill, DesktopPillText, DesktopButton, desktop);
        SetCard(CodePill, CodePillText, CodeButton, code);
    }

    private static void SetCard(Border pill, TextBlock pillText, Button button, bool on)
    {
        pillText.Text = on ? "Installed" : "Not installed";
        pill.Background = Theme.Brush(on ? "#27500A" : "#633806");
        pillText.Foreground = Theme.Brush(on ? "#C0DD97" : "#FAC775");
        button.Content = on ? "Remove hook" : "Install hook";
    }

    private void DesktopButton_Click(object sender, RoutedEventArgs e) => Toggle(desktop: true);
    private void CodeButton_Click(object sender, RoutedEventArgs e) => Toggle(desktop: false);

    private void Toggle(bool desktop)
    {
        bool want = !HookSwitches.IsOn(_s, desktop);
        if (!HookSwitches.Set(_s, desktop, want))
            MessageBox.Show(this, "Couldn't update ~/.claude/settings.json. See overlay.log for details.", "Claude overlay");
        UpdateHookUi();
    }
}
