using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

internal static class Ui
{
    public static Window MakeWindow(Window? owner, string title, double w, double h)
    {
        var win = new Window
        {
            Title = title, Width = w, Height = h, Owner = owner, ShowInTaskbar = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Style = (Style)Application.Current.FindResource("DarkWindow"), ResizeMode = ResizeMode.CanResizeWithGrip,
        };
        return win;
    }

    public static TextBlock Label(string text, bool muted = false, double top = 8)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, top, 0, 4), TextWrapping = TextWrapping.Wrap };
        if (muted) t.Style = (Style)Application.Current.FindResource("Muted");
        return t;
    }

    public static StackPanel OkCancel(Window win, Func<bool> onOk)
    {
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("AccentButton") };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90 };
        ok.Click += (_, _) => { if (onOk()) win.DialogResult = true; };
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        p.Children.Add(ok); p.Children.Add(cancel);
        return p;
    }

    public static string? Input(Window? owner, string title, string prompt, string value = "")
    {
        var win = MakeWindow(owner, title, 420, 190);
        var box = new TextBox { Text = value };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(Label(prompt, top: 0));
        sp.Children.Add(box);
        sp.Children.Add(OkCancel(win, () => box.Text.Trim().Length > 0));
        win.Content = sp;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text.Trim() : null;
    }

    public static string ComboFromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var parts = new List<string>();
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (m.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (m.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (m.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
            parts.Add(KeyCombo.VkName((ushort)KeyInterop.VirtualKeyFromKey(key)));
        return string.Join("+", parts);
    }
}

/// <summary>Editor for one HostAction (kind + value).</summary>
internal sealed class HostActionPanel : StackPanel
{
    private static readonly (HostActionKind Kind, string Title, string Hint)[] Kinds =
    {
        (HostActionKind.None, "Нет действия", ""),
        (HostActionKind.LaunchProgram, "Запустить программу", "Путь к .exe или файлу"),
        (HostActionKind.OpenUrl, "Открыть ссылку", "https://…"),
        (HostActionKind.Text, "Напечатать текст", "Текст"),
        (HostActionKind.KeyCombo, "Сочетание клавиш (через ПК)", "Например Ctrl+Shift+Esc"),
        (HostActionKind.SwitchProfile, "Переключить профиль", "Имя профиля"),
    };
    private readonly ComboBox _kind = new();
    private readonly TextBox _value = new();
    private readonly TextBox _args = new();
    private readonly TextBlock _hint;
    private readonly Button _browse = new() { Content = "Обзор…", Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _capture = new() { Content = "Захватить", Margin = new Thickness(8, 0, 0, 0) };
    private bool _capturing;

    public HostActionPanel(HostAction a, IEnumerable<string> profiles)
    {
        foreach (var k in Kinds) _kind.Items.Add(k.Title);
        _kind.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.Kind == a.Kind));
        _value.Text = a.Value; _args.Text = a.Arguments;
        _hint = Ui.Label("", true, 6);
        Children.Add(_kind); Children.Add(_hint);
        var row = new DockPanel();
        DockPanel.SetDock(_browse, Dock.Right); DockPanel.SetDock(_capture, Dock.Right);
        row.Children.Add(_browse); row.Children.Add(_capture); row.Children.Add(_value);
        Children.Add(row);
        var argsLabel = Ui.Label("Аргументы командной строки (необязательно)", true, 6);
        Children.Add(argsLabel); Children.Add(_args);
        var profileList = profiles.ToList();
        _kind.SelectionChanged += (_, _) => Refresh(argsLabel);
        _browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Программы|*.exe;*.bat;*.cmd;*.lnk|Все файлы|*.*" };
            if (dlg.ShowDialog() == true) _value.Text = dlg.FileName;
        };
        _capture.Click += (_, _) => { _capturing = true; _value.Text = "нажмите сочетание…"; _value.Focus(); };
        _value.PreviewKeyDown += (_, e) =>
        {
            if (!_capturing) return;
            e.Handled = true;
            var combo = Ui.ComboFromKeyEvent(e);
            _value.Text = combo;
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
                _capturing = false;
        };
        if (profileList.Count > 0) _value.ToolTip = "Профили: " + string.Join(", ", profileList);
        Refresh(argsLabel);
    }

    private void Refresh(TextBlock argsLabel)
    {
        var k = Kinds[Math.Max(0, _kind.SelectedIndex)];
        _hint.Text = k.Hint;
        _value.IsEnabled = k.Kind != HostActionKind.None;
        _browse.Visibility = k.Kind == HostActionKind.LaunchProgram ? Visibility.Visible : Visibility.Collapsed;
        _capture.Visibility = k.Kind == HostActionKind.KeyCombo ? Visibility.Visible : Visibility.Collapsed;
        var argsVis = k.Kind == HostActionKind.LaunchProgram ? Visibility.Visible : Visibility.Collapsed;
        _args.Visibility = argsVis; argsLabel.Visibility = argsVis;
    }

    public bool TryGet(out HostAction action, out string error)
    {
        var k = Kinds[Math.Max(0, _kind.SelectedIndex)].Kind;
        action = new HostAction { Kind = k, Value = k == HostActionKind.None ? "" : _value.Text.Trim(), Arguments = k == HostActionKind.LaunchProgram ? _args.Text.Trim() : "" };
        error = "";
        if (k != HostActionKind.None && action.Value.Length == 0) { error = "Укажите значение действия."; return false; }
        if (k == HostActionKind.KeyCombo)
        {
            try { KeyCombo.Parse(action.Value); } catch (Exception ex) { error = ex.Message; return false; }
        }
        return true;
    }
}

/// <summary>Text descriptions of actions.</summary>
internal static class ActionText
{
    public static string MacroSummary(MacroSlot m)
    {
        var combo = Labels.ComboOf(m);
        if (combo != null) return combo;
        var mouse = Labels.MouseOf(m);
        if (mouse != null) return mouse;
        var s = string.Join(" ", m.Events.Take(8).Select(e => e.IsWheel ? Labels.EventText(e) : (e.Down ? "↓" : "↑") + Labels.EventText(e)));
        return m.Events.Count > 8 ? s + " …" : s;
    }

    public static string Describe(byte code, K13DeviceConfig cfg, HostSettings host)
    {
        if (KeyCatalog.IsMacro(code))
        {
            int slot = KeyCatalog.MacroSlotOf(code);
            var m = cfg.Macros[slot];
            return $"Макрос {slot + 1}" + (m.IsUsed ? $": {MacroSummary(m)}" : " (пустой)");
        }
        if (code is >= 0x68 and <= 0x73)
        {
            var b = host.Keys.FirstOrDefault(k => k.TriggerVk == 0x7C + (code - 0x68));
            if (b != null && (b.Tap.Kind != HostActionKind.None || b.Hold.Kind != HostActionKind.None))
                return $"{b.Tap}" + (b.Hold.Kind != HostActionKind.None ? $"; удержание: {b.Hold}" : "") + $"  (через ПК, {KeyCatalog.Name(code)})";
        }
        return KeyCatalog.Name(code);
    }
}

/// <summary>Dialog for a host trigger binding (tap/hold).</summary>
internal static class HostBindingEditor
{
    public static bool Edit(Window owner, HostKeyBinding b, IEnumerable<string> profiles)
    {
        var win = Ui.MakeWindow(owner, "Клавиша-триггер", 560, 560);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Label("Клавиша-триггер (что посылает K13)", top: 0));
        var trig = new ComboBox();
        for (int i = 0; i < 12; i++) trig.Items.Add(new ComboBoxItem { Content = "F" + (13 + i), Tag = (ushort)(0x7C + i) });
        if (b.TriggerVk != 0 && b.TriggerVk is < 0x7C or > 0x87) trig.Items.Add(new ComboBoxItem { Content = KeyCombo.VkName(b.TriggerVk), Tag = b.TriggerVk });
        foreach (ComboBoxItem it in trig.Items) if ((ushort)it.Tag! == b.TriggerVk) trig.SelectedItem = it;
        if (trig.SelectedIndex < 0) trig.SelectedIndex = 0;
        root.Children.Add(trig);
        root.Children.Add(Ui.Label("Короткое нажатие"));
        var tap = new HostActionPanel(b.Tap, profiles); root.Children.Add(tap);
        root.Children.Add(Ui.Label("Удержание (необязательно)"));
        var hold = new HostActionPanel(b.Hold, profiles); root.Children.Add(hold);
        root.Children.Add(Ui.Label("Порог удержания, мс"));
        var ms = new TextBox { Text = b.HoldMs.ToString(), Width = 100, HorizontalAlignment = HorizontalAlignment.Left }; root.Children.Add(ms);
        var sup = new CheckBox { Content = "Не передавать исходную клавишу в систему", IsChecked = b.Suppress, Margin = new Thickness(0, 10, 0, 0) }; root.Children.Add(sup);
        var err = new TextBlock { Foreground = (Brush)Application.Current.FindResource("ErrBrush") }; root.Children.Add(err);
        root.Children.Add(Ui.OkCancel(win, () =>
        {
            if (!tap.TryGet(out var t, out var e1)) { err.Text = e1; return false; }
            if (!hold.TryGet(out var h, out var e2)) { err.Text = e2; return false; }
            if (!int.TryParse(ms.Text, out var m) || m < 100 || m > 5000) { err.Text = "Порог: 100–5000 мс."; return false; }
            b.TriggerVk = (ushort)((ComboBoxItem)trig.SelectedItem).Tag!; b.Tap = t; b.Hold = h; b.HoldMs = m; b.Suppress = sup.IsChecked == true;
            return true;
        }));
        win.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return win.ShowDialog() == true;
    }

    public static bool EditChord(Window owner, HostChordBinding c, IEnumerable<string> profiles)
    {
        var win = Ui.MakeWindow(owner, "Аккорд (диагональ)", 520, 430);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(Ui.Label("Название", top: 0));
        var title = new TextBox { Text = c.Title }; root.Children.Add(title);
        ComboBox Trig(ushort vk)
        {
            var cb = new ComboBox();
            for (int i = 0; i < 12; i++) { var it = new ComboBoxItem { Content = "F" + (13 + i), Tag = (ushort)(0x7C + i) }; cb.Items.Add(it); if (0x7C + i == vk) cb.SelectedItem = it; }
            if (cb.SelectedIndex < 0) cb.SelectedIndex = 0;
            return cb;
        }
        root.Children.Add(Ui.Label("Первая клавиша")); var a = Trig(c.VkA); root.Children.Add(a);
        root.Children.Add(Ui.Label("Вторая клавиша")); var bb = Trig(c.VkB); root.Children.Add(bb);
        root.Children.Add(Ui.Label("Действие при одновременном нажатии"));
        var act = new HostActionPanel(c.Action, profiles); root.Children.Add(act);
        var err = new TextBlock { Foreground = (Brush)Application.Current.FindResource("ErrBrush") }; root.Children.Add(err);
        root.Children.Add(Ui.OkCancel(win, () =>
        {
            if (!act.TryGet(out var x, out var e)) { err.Text = e; return false; }
            var va = (ushort)((ComboBoxItem)a.SelectedItem).Tag!; var vb = (ushort)((ComboBoxItem)bb.SelectedItem).Tag!;
            if (va == vb) { err.Text = "Клавиши должны различаться."; return false; }
            c.Title = title.Text.Trim(); c.VkA = va; c.VkB = vb; c.Action = x;
            return true;
        }));
        win.Content = root;
        return win.ShowDialog() == true;
    }
}
