using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.4: profiles that switch by the active program — «Включать в программах» on each profile card,
/// status «Активен: … — почему», fallback profile, pause (also from the tray), program picker with icons.</summary>
public partial class MainWindow
{
    private string _activeWhy = "";
    private TextBlock? _fgLabel;
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private (string Process, string Title)? _demoForeground;

    private List<AppSwitchRule> RulesOf(AppProfile p) =>
        _store.Data.AppRules.Where(r => string.Equals(r.ProfileName, p.Name, StringComparison.OrdinalIgnoreCase) && AppMatch.IsUsable(r)).ToList();

    private void OnAutoDecided(AutoSwitchDecision d, string proc, string title)
    {
        if (_offline || _exiting) return;
        if (string.Equals(d.Profile, _profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (_activeWhy != d.Reason) { _activeWhy = d.Reason; BuildAutoSwitchCard(); }
            return;
        }
        App.Log($"[auto] {proc} «{Trim(title, 60)}» → {d.Profile} ({d.Reason})");
        _autoFallback = d.IsFallback;
        ActivateProfile(d.Profile, "авто", d.Reason, auto: true);
    }

    /// <summary>Auto-switch never writes to the keypad: say so when the keypad holds other buttons/lighting.</summary>
    private string DeviceNote()
    {
        if (_deviceImage == null || _offline) return "";
        try
        {
            K13MemoryCodec.Encode(DeviceTarget, _deviceImage, out var plan);
            if (K13Protocol.DiffPlan(plan, _deviceImage).Count == 0) return "";
            return _store.Data.PcLayer.Active ? "Кнопки уже переключены (через ПК); подсветка в клавиатуре прежняя — «Записать», если нужно." : "Кнопки и подсветка в клавиатуре прежние — «Записать», если нужно.";
        }
        catch { return ""; }
    }

    internal void SetAutoPaused(bool paused)
    {
        _fg.Engine.Paused = paused;
        if (!paused) _fg.Engine.Reset();
        Log(paused ? "Автопереключение профилей на паузе." : "Автопереключение профилей продолжено.");
        RefreshAutoStatus();
        BuildAutoSwitchCard();
    }

    private void RefreshAutoStatus()
    {
        _tray?.SetAutoSwitch(_store.Data.AutoSwitch, _fg.Engine.Paused);
        _tray?.SetTooltip($"VOROTEX K13 — {_profile.Name}" + (_store.Data.AutoSwitch && _fg.Engine.Paused ? " (автопереключение на паузе)" : ""));
    }

    /// <summary>Muted text after the chip: why it is active and the last real program in front (never our own window).</summary>
    private void RefreshForegroundLabel()
    {
        if (_fgLabel == null) return;
        var (p, t) = _demoForeground ?? _fg.Current;
        string why = _activeWhy.Length > 0 ? _activeWhy : "последний использованный";
        string prog = p.Length > 0 ? p + ".exe" : t.Length > 0 ? "«" + Trim(t, 40) + "»" : "";
        bool mentions = prog.Length > 0 && why.Contains(p.Length > 0 ? p : t, StringComparison.OrdinalIgnoreCase);
        string text = prog.Length == 0 || mentions ? why : $"{why} · {prog}";
        if (_fgLabel.Text != text) _fgLabel.Text = text;
        _fgLabel.ToolTip = p.Length > 0 || t.Length > 0
            ? $"На переднем плане: {(p.Length > 0 ? p + ".exe" : "программа без доступа к имени")}" + (t.Length > 0 ? $"\n«{Trim(t, 90)}»" : "") + "\nОкно самой программы не учитывается."
            : null;
    }

    private static string Trim(string s, int n) => s.Length > n ? s[..n].TrimEnd() + "…" : s;

    // ───────────────────────────── the card above the profiles (v5.6: calm, one line per setting)
    private const double RowLabelWidth = 190;

    /// <summary>v5.7.2: the slim header — «Автопереключение [switch] (?)» next to «+ Новый» and ⋯,
    /// one muted line «Сейчас «Фото» — по программе Photoshop.exe» under the title. Everything else is in ⋯.</summary>
    private void BuildAutoSwitchCard()
    {
        var host = AutoSwitchHost;
        host.Children.Clear();
        var d = _store.Data;
        bool paused = d.AutoSwitch && _fg.Engine.Paused;

        if (paused)
        {
            var resume = new Button { Style = (Style)FindResource("LinkButton"), Content = "на паузе — продолжить", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "Снова переключать профили по программам" };
            resume.Click += (_, _) => SetAutoPaused(false);
            host.Children.Add(resume);
        }
        host.Children.Add(new TextBlock { Text = "Автопереключение", FontSize = 13, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = d.AutoSwitch, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0),
            ToolTip = d.AutoSwitch ? "Выключить автопереключение по программам" : "Включить автопереключение по программам" };
        sw.Checked += (_, _) => SetAutoSwitch(true);
        sw.Unchecked += (_, _) => SetAutoSwitch(false);
        host.Children.Add(sw);
        host.Children.Add(InfoDot(AutoSwitchHelp));

        ActiveStatusHost.Children.Clear();
        ActiveStatusHost.Children.Add(StatusLine(d));

        // one-time hint (quiet line above the cards)
        AutoHintHost.Children.Clear();
        if (!d.AutoSwitchHintSeen)
        {
            var hint = new DockPanel { Margin = new Thickness(2, 0, 14, 14) };
            var ok = new Button { Style = (Style)FindResource("LinkButton"), Content = "Понятно", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand };
            ok.Click += (_, _) => { d.AutoSwitchHintSeen = true; SaveStore(); BuildAutoSwitchCard(); };
            DockPanel.SetDock(ok, Dock.Right);
            hint.Children.Add(ok);
            hint.Children.Add(new TextBlock { Text = "\uE946", FontFamily = Icons, FontSize = 12, Foreground = R("MutedBrush"), Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top });
            hint.Children.Add(new TextBlock { Text = Layer.Active ? "При смене профиля сразу меняются кнопки (через ПК) и действия на ПК. Подсветка в клавиатуре — только по «Записать»."
                                                                 : "При смене профиля сразу меняются действия на ПК. Кнопки — тоже, если включить «Кнопки по профилю» (в ⋯); иначе они в клавиатуре и меняются по «Записать».",
                FontSize = 12, Foreground = R("MutedBrush"), TextWrapping = TextWrapping.Wrap });
            AutoHintHost.Children.Add(hint);
        }
        _tray?.SetAutoSwitch(d.AutoSwitch, _fg.Engine.Paused);
    }

    /// <summary>⋯ next to «+ Новый»: import/export, then the rarely changed auto-switch settings.</summary>
    private void ProfilesMore_Click(object sender, RoutedEventArgs e) => ProfilesMenu(ProfilesMoreBtn).IsOpen = true;

    internal ContextMenu ProfilesMenu(FrameworkElement target)
    {
        var d = _store.Data;
        var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Make(string glyph, string text, Action? click, bool enabled = true, string? note = null)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, Width = 26, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            if (note != null) header.Children.Add(new TextBlock { Text = "  " + note, Foreground = R("MutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            if (click != null) mi.Click += (_, _) => click();
            return mi;
        }
        const string check = "\uE73E";

        m.Items.Add(Make("\uE8B5", "Импорт из файла…", () => ImportProfileJson_Click(this, new RoutedEventArgs()), note: "или перетащите файл"));
        m.Items.Add(Make("\uE77F", "Вставить профиль из буфера", () => PasteProfile_Click(this, new RoutedEventArgs()), note: "Ctrl+V"));
        m.Items.Add(Make("\uEDE1", "Экспорт всех профилей…", () => ExportAll_Click(this, new RoutedEventArgs())));
        m.Items.Add(new Separator());

        // «В других программах» ▸
        const string keep = "Оставлять текущий профиль";
        var fb = Make("\uE8AB", "В других программах", null, d.AutoSwitch, d.FallbackProfile.Length > 0 ? d.FallbackProfile : "текущий");
        fb.Tag = "fallback";
        foreach (var name in new[] { keep }.Concat(d.Profiles.Select(p => p.Name)))
        {
            string target2 = name == keep ? "" : name;
            bool sel = string.Equals(d.FallbackProfile, target2, StringComparison.OrdinalIgnoreCase);
            fb.Items.Add(Make(sel ? check : "", name, () =>
            {
                d.FallbackProfile = target2;
                SaveStore(); _fg.Engine.Reset();
                Log(d.FallbackProfile.Length > 0 ? $"Профиль по умолчанию (нет правила): «{d.FallbackProfile}»." : "Без правила профиль больше не меняется.");
                Dispatcher.BeginInvoke(RefreshProfilesList, System.Windows.Threading.DispatcherPriority.Background);
            }));
        }
        m.Items.Add(fb);
        if (d.AutoSwitch)
            m.Items.Add(Make(_fg.Engine.Paused ? "\uE768" : "\uE769", _fg.Engine.Paused ? "Продолжить автопереключение" : "Пауза автопереключения", () => SetAutoPaused(!_fg.Engine.Paused)));

        // «Уведомление при смене» ▸
        var toast = Make("\uEA8F", "Уведомление при смене", null, true, d.SwitchToast ? SwitchToastLogic.PlacementName(d.ToastPlacement).ToLowerInvariant() : "выключено");
        toast.Tag = "toast";
        toast.Items.Add(Make(d.SwitchToast ? check : "", "Показывать", () =>
        {
            bool v = !d.SwitchToast;
            SetToastSettings(enabled: v);
            Log(v ? "Уведомление при смене профиля включено." : "Уведомление при смене профиля выключено.");
            if (!v) _toast?.HideNow();
            Dispatcher.BeginInvoke(RefreshProfilesList, System.Windows.Threading.DispatcherPriority.Background);
        }));
        toast.Items.Add(new Separator());
        foreach (var pl in Enum.GetValues<ToastPlacement>())
            toast.Items.Add(Make(d.ToastPlacement == pl ? check : "", SwitchToastLogic.PlacementName(pl), () =>
            {
                SetToastSettings(placement: pl);
                Log("Уведомление о смене профиля: " + SwitchToastLogic.PlacementName(d.ToastPlacement).ToLowerInvariant() + ".");
                ShowToastPreview();
            }, d.SwitchToast));
        toast.Items.Add(new Separator());
        foreach (var o in ToastMenu(target).Items.OfType<MenuItem>().ToList())
        {
            ((ContextMenu)o.Parent).Items.Remove(o);
            toast.Items.Add(o);
        }
        m.Items.Add(toast);

        // «Кнопки по профилю (через ПК)» — a confirm dialog comes first, as with the old switch
        bool on = Layer.Active;
        m.Items.Add(Make(on ? check : "", "Кнопки по профилю (через ПК)", async () => { if (Layer.Active) await RestoreKeypad(); else await PrepareKeypad(); BuildAutoSwitchCard(); },
            true, on ? RuPlural.Of(Layer.Keys.Count, "кнопка", "кнопки", "кнопок") : "выключено").With(x => { x.Tag = "pcmode"; x.ToolTip = PcModeHelp; }));
        return m;
    }

    private const string AutoSwitchHelp =
        "Профиль включается сам, когда вы переходите в программу из его списка «Включать в программах» (на карточке профиля). Профиль для остальных программ, пауза и уведомление — в ⋯ рядом с «Новый».\n\n" +
        "Сразу меняются действия на ПК: кольцо, сочетания, запуск программ и ссылок, макросы на компьютере (F13–F24).\n\n" +
        "Кнопки меняются вместе с профилем, если включены «Кнопки по профилю» (через ПК). Иначе кнопки, как и подсветка, хранятся в самой клавиатуре и меняются только по «Записать» — " +
        "автопереключение в клавиатуру никогда не пишет (частая запись изнашивает её память).";

    /// <summary>Label column + control, the same height and spacing for every setting.</summary>
    private FrameworkElement SettingRow(string label, params UIElement[] controls)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0), MinHeight = 34 };
        var l = new TextBlock { Text = label, Width = RowLabelWidth, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Foreground = R("TextBrush") };
        DockPanel.SetDock(l, Dock.Left);
        row.Children.Add(l);
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in controls) sp.Children.Add(c);
        row.Children.Add(sp);
        return row;
    }

    /// <summary>Small round «?» — hover shows a plain tooltip, click opens a popup that closes on any outside click.
    /// (A ToolTip with StaysOpen=false set through the ToolTip property throws on real Windows — v5.6 bug.)</summary>
    private FrameworkElement InfoDot(string text)
    {
        var b = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(0), Width = 22, Height = 22, Margin = new Thickness(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Help,
            ToolTip = new TextBlock { Text = text, MaxWidth = 340, TextWrapping = TextWrapping.Wrap },
            Content = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.2), BorderBrush = R("MutedBrush"),
                Child = new TextBlock { Text = "?", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = R("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) } } };
        b.SetValue(AutomationHelpKey, text);
        ToolTipService.SetInitialShowDelay(b, 150);
        ToolTipService.SetShowDuration(b, 30000);
        var pop = new System.Windows.Controls.Primitives.Popup
        {
            StaysOpen = false, AllowsTransparency = true,
            PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 4,
            Child = new Border { Background = R("PanelBrush"), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 8),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.18 },
                Child = new TextBlock { Text = text, MaxWidth = 340, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Foreground = R("TextBrush") } }
        };
        long closedAt = 0;
        pop.Opened += (_, _) => ToolTipService.SetIsEnabled(b, false);
        pop.Closed += (_, _) => { closedAt = Environment.TickCount64; ToolTipService.SetIsEnabled(b, true); };
        // the outside-click that closes the popup is the same click on «?» — don't reopen it right away
        b.Click += (_, _) => { if (Environment.TickCount64 - closedAt > 250) pop.IsOpen = true; };
        b.Tag = pop;
        return b;
    }

    /// <summary>v5.7: «Дополнительно ▾» — a quiet link that unfolds rarely used options (collapsed by default).</summary>
    internal FrameworkElement Advanced(FrameworkElement content, string title = "Дополнительно", bool open = false)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var link = new Button { Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, FontSize = 13 };
        content.Margin = new Thickness(0, 4, 0, 0);
        void Sync() { link.Content = title + (content.Visibility == Visibility.Visible ? " ▴" : " ▾"); }
        content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        link.Click += (_, _) => { content.Visibility = content.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; Sync(); };
        Sync();
        sp.Children.Add(link); sp.Children.Add(content);
        sp.Tag = "advanced";
        return sp;
    }

    /// <summary>Marks «?» buttons (value = help text) so the smoke test can find and click them.</summary>
    internal static readonly DependencyProperty AutomationHelpKey = DependencyProperty.RegisterAttached("InfoDotHelp", typeof(string), typeof(MainWindow));

    /// <summary>v5.7.2: one muted line under «Профили»: «Сейчас: Фото — по программе Photoshop.exe».</summary>
    private FrameworkElement StatusLine(ProfileStoreData d)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = $"Сейчас: {_profile.Name}", FontSize = 12.5, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Активный профиль — " + (_activeWhy.Length > 0 ? _activeWhy : "последний использованный") });
        line.Children.Add(new TextBlock { Text = " — ", FontSize = 12.5, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        _fgLabel = new TextBlock { FontSize = 12.5, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 };
        line.Children.Add(_fgLabel);
        RefreshForegroundLabel();
        return line;
    }

    private void SetAutoSwitch(bool on)
    {
        if (_loading || _store.Data.AutoSwitch == on) return;
        _store.Data.AutoSwitch = on; SaveStore();
        _fg.Engine.Reset();
        Log(on ? "Автопереключение профилей включено." : "Автопереключение профилей выключено.");
        Dispatcher.BeginInvoke(() => { BuildAutoSwitchCard(); RefreshAutoStatus(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ───────────────────────────── «Включать в программах» on a profile card
    private FrameworkElement AppRulesSection(AppProfile p, Brush accent)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var add = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(9, 3, 10, 4), ToolTip = "Добавить программу, в которой этот профиль включается сам",
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
            {
                new TextBlock { Text = "\uE710", FontFamily = Icons, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                new TextBlock { Text = "Программа", FontSize = 12 },
            } } };
        add.Click += (_, e) => { e.Handled = true; BuildAppPicker(p).ShowDialog(); };
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(new TextBlock { Text = "Включать в программах", FontSize = 12.5, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center }); // v5.7: quieter label
        sp.Children.Add(head);

        var rules = RulesOf(p);
        if (rules.Count == 0)
        {
            bool fb = string.Equals(_store.Data.FallbackProfile, p.Name, StringComparison.OrdinalIgnoreCase);
            sp.Children.Add(T(fb ? "Во всех остальных." : "Пока нет — только вручную.", 12.5));
            return sp;
        }
        var wrap = new WrapPanel();
        foreach (var r in rules) wrap.Children.Add(RuleChip(r));
        sp.Children.Add(wrap);
        if (!_store.Data.AutoSwitch) sp.Children.Add(T("Автопереключение выключено — правила сейчас не действуют.", 11.5, muted: true).With(t => t.Margin = new Thickness(0, 2, 0, 0)));
        return sp;
    }

    private Border RuleChip(AppSwitchRule r)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(AppIcon(r.ProcessName, r.IconPath, 18, r.DisplayName).With(x => x.Margin = new Thickness(0, 0, 7, 0)));
        var label = new TextBlock { Text = AppMatch.Label(r), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
        row.Children.Add(label);
        if (r.TitleContains.Trim().Length > 0 && AppMatch.NormalizeProcess(r.ProcessName).Length > 0)
            row.Children.Add(new TextBlock { Text = $" · «{Trim(r.TitleContains.Trim(), 18)}»", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        var x = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(4, 0, -2, 0), MinWidth = 0, MinHeight = 0, ToolTip = "Убрать",
            Content = new TextBlock { Text = "\uE711", FontFamily = Icons, FontSize = 9, Foreground = R("MutedBrush") } };
        x.Click += (_, e) =>
        {
            e.Handled = true;
            _store.Data.AppRules.Remove(r); SaveStore(); _fg.Engine.Reset();
            Log($"«{r.ProfileName}» больше не включается в {AppMatch.Describe(r)}.");
            Dispatcher.BeginInvoke(RefreshProfilesList, System.Windows.Threading.DispatcherPriority.Background);
        };
        row.Children.Add(x);
        return new Border { Child = row, Background = R("Panel2Brush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6),
            ToolTip = "Профиль включается: " + AppMatch.Describe(r) };
    }

    /// <summary>Program icon from its .exe (cached), or a coloured letter tile when the file is not available.</summary>
    private FrameworkElement AppIcon(string process, string? exePath, double size, string? display = null)
    {
        var proc = AppMatch.NormalizeProcess(process);
        ImageSource? img = null;
        if (!_offline && !string.IsNullOrEmpty(exePath))
        {
            if (!_iconCache.TryGetValue(exePath, out img))
            {
                try
                {
                    if (File.Exists(exePath))
                    {
                        using var ic = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                        if (ic != null)
                        {
                            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze(); img = bs;
                        }
                    }
                }
                catch { img = null; }
                _iconCache[exePath] = img;
            }
        }
        if (img != null) return new Image { Source = img, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var name = (display ?? "").Trim().Length > 0 ? display!.Trim() : proc.Length > 0 ? proc : "?";
        int h = 0; foreach (var ch in proc.ToLowerInvariant()) h = h * 31 + ch;
        var color = (Color)ColorConverter.ConvertFromString(AccentPalette[Math.Abs(h % AccentPalette.Length)]);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = proc.Length == 0 ? "\uE7C4" : name[..1].ToUpperInvariant(), FontFamily = proc.Length == 0 ? Icons : FontFamily, FontSize = size * 0.55, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    }

    // ───────────────────────────── program picker
    private sealed record RunningApp(string Process, string Path, string Title, string Display);

    private List<RunningApp> RunningApps()
    {
        if (_offline)
            return new()
            {
                new("Photoshop", "", "Безымянный-1 @ 66,7% (RGB/8)", "Adobe Photoshop 2025"),
                new("chrome", "", "YouTube — Google Chrome", "Google Chrome"),
                new("cs2", "", "Counter-Strike 2", "Counter-Strike 2"),
                new("Telegram", "", "Telegram", "Telegram Desktop"),
                new("Code", "", "MainWindow.cs — studio — Visual Studio Code", "Visual Studio Code"),
                new("explorer", "", "Загрузки", "Проводник"),
            };
        var list = new List<RunningApp>();
        try
        {
            foreach (var (proc, path, title) in Native.RunningApps())
            {
                if (AppMatch.IsOwn(proc) || AppMatch.Transient.Contains(proc)) continue;
                string disp = "";
                try { if (path.Length > 0) disp = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
                list.Add(new(proc, path, title, disp));
            }
        }
        catch (Exception ex) { Log("Список программ: " + ex.Message); }
        return list.OrderBy(a => (a.Display.Length > 0 ? a.Display : a.Process), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal Window BuildAppPicker(AppProfile p, string? preselect = null)
    {
        var w = Ui.MakeWindow(IsLoaded ? this : null, "Включать в программе", 600, 660);
        w.MinWidth = 520; w.MinHeight = 520;
        var root = new DockPanel { Margin = new Thickness(24, 12, 24, 20) };

        var top = new StackPanel();
        top.Children.Add(T($"Включать «{p.Name}» в программе", 18, semi: true, bottom: 4));
        top.Children.Add(T("Выберите запущенную программу, укажите её файл .exe или введите имя процесса.", 13, muted: true, bottom: 12));
        var search = new TextBox { Padding = new Thickness(10, 6, 10, 6) };
        var searchHint = new TextBlock { Text = "Поиск среди запущенных программ", Foreground = R("MutedBrush"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        search.TextChanged += (_, _) => searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        top.Children.Add(new Grid { Children = { search, searchHint }, Margin = new Thickness(0, 0, 0, 8) });
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // bottom: manual entry, title filter, errors, buttons
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var manual = new Grid();
        manual.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        manual.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var procBox = new TextBox { Padding = new Thickness(10, 6, 10, 6), Text = preselect ?? "" };
        var procHint = new TextBlock { Text = "имя процесса, например photoshop", Foreground = R("MutedBrush"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Visibility = procBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        manual.Children.Add(procBox); manual.Children.Add(procHint);
        var browse = new Button { Style = (Style)FindResource("GhostButton"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
            {
                new TextBlock { Text = "\uE8E5", FontFamily = Icons, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                new TextBlock { Text = "Файл .exe…" },
            } } };
        Grid.SetColumn(browse, 1); manual.Children.Add(browse);
        bottom.Children.Add(T("Программа", 13, semi: true, bottom: 6));
        bottom.Children.Add(manual);
        var useTitle = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Только если в заголовке окна есть:", Margin = new Thickness(0, 12, 0, 6) };
        var titleBox = new TextBox { Padding = new Thickness(10, 6, 10, 6), IsEnabled = false };
        useTitle.Checked += (_, _) => { titleBox.IsEnabled = true; titleBox.Focus(); };
        useTitle.Unchecked += (_, _) => titleBox.IsEnabled = false;
        // v5.7: the window-title condition is rare — folded under «Дополнительно»
        var adv = new StackPanel();
        adv.Children.Add(useTitle);
        adv.Children.Add(titleBox);
        adv.Children.Add(T("Например, «YouTube» для вкладки браузера. Если игра или программа, запущенная от администратора, не определяется по имени, оставьте имя пустым и укажите заголовок.", 12, muted: true).With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        bottom.Children.Add(Advanced(adv, "Дополнительно: условие по заголовку окна"));
        var err = T("", 12.5); err.Foreground = R("WarnBrush"); err.Margin = new Thickness(0, 8, 0, 0);
        bottom.Children.Add(err);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Добавить", MinWidth = 110, IsDefault = true, Style = (Style)FindResource("AccentButton"), Padding = new Thickness(16, 7, 16, 7) };
        var cancel = new Button { Content = "Отмена", MinWidth = 96, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        bottom.Children.Add(bar);
        root.Children.Add(bottom);

        // running programs
        var listHost = new StackPanel();
        var scroll = new ScrollViewer { Content = listHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(new Border { Child = scroll, BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(6) });

        var apps = RunningApps();
        string selPath = "", selDisplay = "";
        Border? selRow = null;
        void Fill()
        {
            listHost.Children.Clear();
            var q = search.Text.Trim();
            var shown = apps.Where(a => q.Length == 0 || a.Process.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (shown.Count == 0) { listHost.Children.Add(T(apps.Count == 0 ? "Не удалось получить список программ — укажите файл или имя ниже." : "Ничего не найдено.", 13, muted: true).With(t => t.Margin = new Thickness(10))); return; }
            foreach (var a in shown)
            {
                var row = new DockPanel();
                var owner = _store.Data.AppRules.FirstOrDefault(r => r.TitleContains.Trim().Length == 0 && string.Equals(AppMatch.NormalizeProcess(r.ProcessName), a.Process, StringComparison.OrdinalIgnoreCase));
                if (owner != null)
                {
                    var pill = Pill("", string.Equals(owner.ProfileName, p.Name, StringComparison.OrdinalIgnoreCase) ? "уже в списке" : $"сейчас: «{owner.ProfileName}»", false);
                    pill.VerticalAlignment = VerticalAlignment.Center; pill.Margin = new Thickness(8, 0, 4, 0);
                    DockPanel.SetDock(pill, Dock.Right); row.Children.Add(pill);
                }
                row.Children.Add(AppIcon(a.Process, a.Path, 28, a.Display).With(x => x.Margin = new Thickness(0, 0, 12, 0)));
                var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                tx.Children.Add(new TextBlock { Text = a.Display.Length > 0 ? a.Display : a.Process, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                tx.Children.Add(new TextBlock { Text = $"{a.Process}.exe · {a.Title}", FontSize = 12, Foreground = R("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(tx);
                var b = new Border { Child = row, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2) };
                void Select()
                {
                    if (selRow != null) { selRow.Background = Brushes.Transparent; selRow.BorderBrush = Brushes.Transparent; }
                    selRow = b; b.Background = R("AccentSoftBrush"); b.BorderBrush = R("AccentBrush");
                    procBox.Text = a.Process; selPath = a.Path; selDisplay = a.Display; err.Text = "";
                }
                b.MouseLeftButtonUp += (_, _) => Select();
                b.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Select(); ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); } };
                if (preselect != null && string.Equals(preselect, a.Process, StringComparison.OrdinalIgnoreCase)) Select();
                listHost.Children.Add(b);
            }
        }
        search.TextChanged += (_, _) => Fill();
        procBox.TextChanged += (_, _) =>
        {
            procHint.Visibility = procBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selRow != null && !string.Equals(AppMatch.NormalizeProcess(procBox.Text), apps.FirstOrDefault(a => a.Path == selPath)?.Process ?? "", StringComparison.OrdinalIgnoreCase))
            { selRow.Background = Brushes.Transparent; selRow.BorderBrush = Brushes.Transparent; selRow = null; selPath = ""; selDisplay = ""; }
        };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выберите программу" };
            if (dlg.ShowDialog(w) != true) return;
            procBox.Text = AppMatch.NormalizeProcess(dlg.FileName);
            selPath = dlg.FileName;
            try { selDisplay = System.Diagnostics.FileVersionInfo.GetVersionInfo(dlg.FileName).FileDescription?.Trim() ?? ""; } catch { selDisplay = ""; }
        };
        ok.Click += (_, _) =>
        {
            var msg = AddRule(p, procBox.Text, useTitle.IsChecked == true ? titleBox.Text : "", selDisplay, selPath);
            if (msg != null) { err.Text = msg; return; }
            w.DialogResult = true;
        };
        Fill();
        w.Content = root;
        w.Loaded += (_, _) => search.Focus();
        return w;
    }

    /// <summary>Adds a «Включать в программах» rule; returns an error text or null.</summary>
    internal string? AddRule(AppProfile p, string process, string title, string display = "", string path = "")
    {
        var proc = AppMatch.NormalizeProcess(process);
        title = (title ?? "").Trim();
        if (proc.Length == 0 && title.Length == 0) return "Выберите программу, укажите файл или введите имя процесса (или заголовок окна).";
        if (proc.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0) return "В имени процесса недопустимые символы.";
        if (proc.Length > 120 || title.Length > 200) return "Слишком длинно.";
        if (AppMatch.IsOwn(proc)) return "Это сама VOROTEX K13 Studio — в её окне профиль не переключается, чтобы вы могли спокойно его настраивать.";
        var same = _store.Data.AppRules.FirstOrDefault(r => string.Equals(AppMatch.NormalizeProcess(r.ProcessName), proc, StringComparison.OrdinalIgnoreCase) && string.Equals(r.TitleContains.Trim(), title, StringComparison.OrdinalIgnoreCase));
        if (same != null && string.Equals(same.ProfileName, p.Name, StringComparison.OrdinalIgnoreCase)) return "Эта программа уже в списке профиля.";
        if (same != null)
        {
            _store.Data.AppRules.Remove(same);
            Log($"{AppMatch.Describe(same)}: правило перенесено из «{same.ProfileName}» в «{p.Name}».");
        }
        var rule = new AppSwitchRule { ProcessName = proc, TitleContains = title, ProfileName = p.Name, DisplayName = display.Trim(), IconPath = path };
        _store.Data.AppRules.Add(rule);
        SaveStore(); _fg.Engine.Reset();
        Log($"«{p.Name}» включается: {AppMatch.Describe(rule)}.");
        Dispatcher.BeginInvoke(RefreshProfilesList, System.Windows.Threading.DispatcherPriority.Background);
        return null;
    }

    /// <summary>Screenshots/smoke test (offline store): rules with names, a fallback and a reason.</summary>
    internal void DemoV54()
    {
        if (!_offline) return;
        var d = _store.Data;
        d.AppRules.Clear();
        d.AppRules.Add(new AppSwitchRule { ProcessName = "Photoshop", ProfileName = "Фото", DisplayName = "Adobe Photoshop 2025" });
        d.AppRules.Add(new AppSwitchRule { ProcessName = "Lightroom", ProfileName = "Фото", DisplayName = "Lightroom Classic" });
        d.AppRules.Add(new AppSwitchRule { ProcessName = "cs2", ProfileName = "Игры", DisplayName = "Counter-Strike 2" });
        d.AppRules.Add(new AppSwitchRule { ProcessName = "", TitleContains = "Elden Ring", ProfileName = "Игры" });
        d.AppRules.Add(new AppSwitchRule { ProcessName = "chrome", TitleContains = "YouTube", ProfileName = "Игры", DisplayName = "Google Chrome" });
        d.FallbackProfile = "Основной";
        d.AutoSwitch = true;
        _demoForeground = ("Photoshop", "Безымянный-1 @ 66,7% (RGB/8)");
        ActivateProfile("Фото", "авто", "по программе Photoshop.exe", auto: true);
    }
}
