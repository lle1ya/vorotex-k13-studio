using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>1.2.0 «Проверка кнопок» (like «Тестирование устройства» of the SayoDevice O3C): the schematic lights the control that
/// is held, a short log lists presses and releases. Read-only — nothing is sent to the keypad; the key's action still runs.</summary>
public partial class MainWindow
{
    private Window? _tester;
    private DeviceView? _testerView;
    private ListBox? _testerLog;
    private TextBlock? _testerMode;
    private readonly Dictionary<ushort, List<int>> _testerDown = new();
    private long _scrollLockAt;
    private (ushort Vk, bool Down, long At) _testerLast;
    private ushort _consumerDown;
    internal int TesterEvents { get; private set; }
    private readonly List<string> _testerLines = new();
    internal IReadOnlyList<string> TesterLines => _testerLines;
    internal IReadOnlyCollection<int> TesterLit => _testerDown.Values.SelectMany(x => x).Distinct().ToList();

    internal Window BuildTesterWindow()
    {
        var win = Ui.MakeWindow(IsLoaded ? this : null, "Проверка кнопок", 960, 700);
        var root = new DockPanel { Margin = new Thickness(20) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Нажимайте кнопки и крутите колесо K13", FontSize = 20, FontWeight = FontWeights.SemiBold });
        _testerMode = new TextBlock { FontSize = 12.5, Foreground = R("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
        head.Children.Add(_testerMode);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var limits = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var l in KeyTester.Limits(RoutedState.Active))
            limits.Children.Add(new TextBlock { Text = "• " + l, FontSize = 12, Foreground = R("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var close = new Button { Content = "Закрыть", Style = (Style)FindResource("AccentButton"), MinWidth = 110, IsCancel = true };
        close.Click += (_, _) => win.Close();
        var clear = new Button { Content = "Очистить журнал", Style = (Style)FindResource("GhostButton"), MinWidth = 120, Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) => { _testerLog?.Items.Clear(); _testerLines.Clear(); _testerDown.Clear(); _testerView?.ClearPressed(); };
        DockPanel.SetDock(close, Dock.Right); DockPanel.SetDock(clear, Dock.Right);
        row.Children.Add(close); row.Children.Add(clear); row.Children.Add(new TextBlock());
        limits.Children.Add(row);
        DockPanel.SetDock(limits, Dock.Bottom);
        root.Children.Add(limits);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9, GridUnitType.Star) });
        _testerView = new DeviceView(interactive: false) { ShowSubLabels = false, VerticalAlignment = VerticalAlignment.Top };
        _testerView.Rebuild(KeypadCfg ?? Cfg, _profile.Host, -1, showDots: false);
        grid.Children.Add(_testerView);
        _testerLog = new ListBox { FontSize = 12.5 };
        ScrollViewer.SetHorizontalScrollBarVisibility(_testerLog, ScrollBarVisibility.Disabled);
        Grid.SetColumn(_testerLog, 2);
        grid.Children.Add(_testerLog);
        root.Children.Add(grid);
        win.Content = root;
        TesterModeText();
        return win;
    }

    private void TesterModeText()
    {
        if (_testerMode == null) return;
        var model = K13Model.Detect(_deviceImage);
        string mode = _hwMode switch
        {
            HwMode.Normal => "Режим клавиатуры: Normal — кнопки посылают заводские коды (Num 1…).",
            HwMode.Game => "Режим клавиатуры: Game — кнопки посылают назначенные коды" + (KeypadCfg == null ? " (память ещё не прочитана — узнаю кнопки по профилю)." : "."),
            _ => _connected ? "Режим клавиатуры неизвестен." : "Клавиатура не подключена.",
        };
        _testerMode.Text = mode + (RoutedState.Active ? " Включены «Кнопки по профилю (через ПК)»." : "") +
                           (model is KeypadModel.K12Likely or KeypadModel.Unknown ? " " + K13Model.Banner(model) : "");
    }

    internal void ShowTester()
    {
        if (_tester != null) { _tester.Activate(); return; }
        _tester = BuildTesterWindow();
        bool extra = _k13Keys?.ListenExtra(true) == true;
        Log("[tester] проверка кнопок открыта" + (extra ? " (клавиши, медиа, мышь)" : " (только клавиши)") + ".");
        _tester.Closed += (_, _) =>
        {
            _k13Keys?.ListenExtra(false);
            _tester = null; _testerView = null; _testerLog = null; _testerMode = null; _testerDown.Clear();
            Log($"[tester] проверка кнопок закрыта ({TesterEvents} событий).");
        };
        _tester.Show();
    }

    private static string Who(IReadOnlyCollection<int> cols) =>
        cols.Count == 0 ? "кнопка не найдена в назначениях" : string.Join(" или ", cols.Select(DeviceView.ControlName));

    private void TesterLine(string text)
    {
        TesterEvents++;
        DemoTesterLast = text;
        if (_testerLog == null) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {text}";
        _testerLines.Insert(0, line);
        if (_testerLines.Count > 200) _testerLines.RemoveAt(_testerLines.Count - 1);
        _testerLog.Items.Insert(0, new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        while (_testerLog.Items.Count > 200) _testerLog.Items.RemoveAt(_testerLog.Items.Count - 1);
    }

    private void TesterLight(ushort key, List<int> cols, bool down)
    {
        if (down)
        {
            _testerDown[key] = cols;
            foreach (var c in cols) _testerView?.SetPressed(c, true);
        }
        else if (_testerDown.Remove(key, out var was))
        {
            var still = _testerDown.Values.SelectMany(x => x).ToHashSet();
            foreach (var c in was.Where(c => !still.Contains(c))) _testerView?.SetPressed(c, false);
        }
    }

    /// <summary>A short flash for events without a release (wheel detent, a key the PC layer took).</summary>
    private void TesterFlash(List<int> cols, int ms = 180)
    {
        ushort key = (ushort)(0xF000 + Environment.TickCount % 0x0FFF);
        TesterLight(key, cols, true);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); TesterLight(key, cols, false); };
        t.Start();
    }

    /// <summary>Raw Input key from the keypad (before routing).</summary>
    private long _testerModeAt;
    internal string TesterModeLabel => _testerMode?.Text ?? "";

    internal void TesterKey(ushort vk, bool down)
    {
        if (_tester == null && !_testerForce) return;
        long now = Environment.TickCount64;
        if (_testerLast.Vk == vk && _testerLast.Down == down && now - _testerLast.At < 80 && vk is >= 0xA6 and <= 0xB7) return;   // media key seen twice (keyboard + consumer)
        if (down && _testerDown.ContainsKey(vk)) return;   // auto-repeat
        _testerLast = (vk, down, now);
        if (down && _tester != null && now - _testerModeAt > 700) { _testerModeAt = now; _ = RefreshHwMode("проверка кнопок"); }   // 1.3.1: mode right after a press
        var routed = RoutedState;
        if (routed.Active && vk == PcLayerPlanner.ChordPrefixVk) { if (down) _scrollLockAt = now; return; }
        List<int> cols;
        string how;
        int chord = routed.Active && now - _scrollLockAt < 400 ? KeyTester.ChordColumn(vk, routed) : -1;
        if (chord >= 0) { cols = new() { chord }; how = "через ПК (Scroll Lock + " + VkName(vk) + ")"; }
        else { cols = KeyTester.ControlsFor(vk, _hwMode, KeypadCfg ?? (_hwMode == HwMode.Normal ? null : DeviceTarget)); how = $"«{VkName(vk)}»"; }
        if (down || _testerDown.ContainsKey(vk)) TesterLine($"{(down ? "▼ нажата " : "▲ отпущена")}  {Who(cols)} — {how}");
        TesterLight(vk, cols, down);
    }

    /// <summary>The «через ПК» hook took a keypad key (no Raw Input, no release).</summary>
    internal void TesterRouted(int col)
    {
        if (_tester == null && !_testerForce) return;
        if (col < 0) return;
        TesterLine($"▼ нажата  {DeviceView.ControlName(col)} — через ПК (перехват)");
        TesterFlash(new() { col }, 250);
    }

    internal void TesterConsumer(ushort usage)
    {
        if (_tester == null && !_testerForce) return;
        if (usage == 0)
        {
            if (_consumerDown == 0) return;
            ushort was = _consumerDown; _consumerDown = 0;
            var vkUp = KeyTester.Consumer.TryGetValue(was, out var cu) ? cu.Vk : (ushort)0;
            if (vkUp != 0) TesterKey(vkUp, false);
            else TesterLight((ushort)(0xE000 | was), new(), false);
            return;
        }
        if (usage == _consumerDown) return;
        _consumerDown = usage;
        var vk = KeyTester.Consumer.TryGetValue(usage, out var c) ? c.Vk : (ushort)0;
        if (vk != 0) TesterKey(vk, true);
        else TesterLine($"▼ медиа  {KeyTester.ConsumerName(usage)}");
    }

    internal void TesterMouse(int wheel, ushort buttons)
    {
        if (_tester == null && !_testerForce) return;
        var kp = KeypadCfg ?? DeviceTarget;
        if (wheel != 0)
        {
            var cols = KeyTester.ControlsForScroll(wheel > 0, kp);
            TesterLine($"⟳ прокрутка {(wheel > 0 ? "вверх" : "вниз")}  {Who(cols)}");
            TesterFlash(cols);
        }
        foreach (var (bit, name) in new (ushort, string)[] { (0x1, "ЛКМ нажата"), (0x2, "ЛКМ отпущена"), (0x4, "ПКМ нажата"), (0x8, "ПКМ отпущена"), (0x10, "СКМ нажата"), (0x20, "СКМ отпущена") })
            if ((buttons & bit) != 0) TesterLine($"🖱 {name} — от K13 (макрос с кнопкой мыши)");
    }

    // smoke test: drive the tester without a window or a keypad
    private bool _testerForce;
    internal void DemoTester(bool on)
    {
        _testerForce = on; _testerDown.Clear(); _scrollLockAt = 0; _testerLast = default; _consumerDown = 0;
        if (!on && _tester == null) { _testerView = null; _testerLog = null; _testerMode = null; }
    }
    internal (string Text, IReadOnlyCollection<int> Lit) DemoTesterState => (_testerLog == null ? "" : string.Join("\n", TesterLines), TesterLit);
    internal string? DemoTesterLast { get; private set; }

    /// <summary>Smoke: hold assignment cleanup.</summary>
    internal void DemoClearMacro(int slot, int holdColumn)
    {
        if (slot >= 0 && slot < Cfg.Macros.Count) { Cfg.Macros[slot] = new MacroSlot(); _profile.MacroNames.Remove(slot); }
        Layer.HoldColumn = holdColumn; SaveStore(); PushMapper(); RefreshAll();
    }

    /// <summary>Smoke: pretend a keypad with this image is connected (no device).</summary>
    internal void DemoModel(byte[]? img, bool connected)
    {
        _connected = connected; _modelNoted = KeypadModel.NotRead;
        NoteModel(img);
        if (img == null) _modelNoted = KeypadModel.NotRead;
        RefreshModeBanner();
    }
    internal KeypadModel DemoModelNoted => _modelNoted;
    internal HwMode TesterHwMode => _hwMode;

}
