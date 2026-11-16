using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.5: «Профиль: 1С · по программе 1cv8c.exe» notice when the profile changes by itself.</summary>
public partial class MainWindow
{
    private SwitchToastWindow? _toast;
    private bool _autoFallback;

    /// <summary>Called by ActivateProfile. True when a notice (own or Windows) was shown.</summary>
    private bool MaybeToast(AppProfile p, SwitchSource source, bool changed)
    {
        var d = _store.Data;
        if (_offline || _exiting || !SwitchToastLogic.ShouldShow(d.SwitchToast, source, changed)) return false;
        var c = SwitchToastLogic.For(p.Name, _activeWhy, source, source == SwitchSource.Auto && _autoFallback);
        if (c == null) return false;
        ShowToast(p, c);
        return true;
    }

    private void ShowToast(AppProfile p, ToastContent c)
    {
        try
        {
            int idx = Math.Max(0, _store.Data.Profiles.IndexOf(p));
            _toast ??= new SwitchToastWindow();
            _toast.Show(c, AccentOf(p, idx), GlyphOf(p, idx), _store.Data.ToastPlacement);
            if (_store.Data.ToastSystem) _tray?.Balloon(c.Title, c.Detail.Length > 0 ? c.Detail : "Действия на ПК переключены.");
        }
        catch (Exception ex) { App.Log("[toast] не показано: " + ex.Message); }
    }

    /// <summary>«Показать пример» and the smoke test.</summary>
    internal SwitchToastWindow ShowToastPreview(string? detail = null)
    {
        var c = new ToastContent(_profile.Name, detail ?? (_activeWhy.Length > 0 ? _activeWhy : "по программе 1cv8c.exe"), false);
        int idx = Math.Max(0, _store.Data.Profiles.IndexOf(_profile));
        _toast ??= new SwitchToastWindow();
        _toast.Show(c, AccentOf(_profile, idx), GlyphOf(_profile, idx), _store.Data.ToastPlacement);
        return _toast;
    }

    internal FrameworkElement ToastCard(string profile, string detail)
    {
        var p = _store.Find(profile) ?? _profile;
        int idx = Math.Max(0, _store.Data.Profiles.IndexOf(p));
        return SwitchToastWindow.BuildCard(new ToastContent(p.Name, detail, false), AccentOf(p, idx), GlyphOf(p, idx));
    }

    internal void SetToastSettings(bool? enabled = null, ToastPlacement? placement = null, bool? system = null)
    {
        var d = _store.Data;
        if (enabled is bool e) d.SwitchToast = e;
        if (placement is ToastPlacement pl) d.ToastPlacement = pl;
        if (system is bool s) d.ToastSystem = s;
        SaveStore();
    }

    /// <summary>The «Уведомление» row at the bottom of the auto-switch card.</summary>
    private FrameworkElement ToastSettingsRow()
    {
        var d = _store.Data;
        var sp = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        sp.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 0, 0, 12) }.With(b => b.SetResourceReference(Border.BackgroundProperty, "BorderBrush")));
        var row = new DockPanel();
        var preview = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 5, 12, 5), VerticalAlignment = VerticalAlignment.Center, IsEnabled = d.SwitchToast,
            ToolTip = "Показать, как будет выглядеть уведомление",
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
            {
                new TextBlock { Text = "\uE7B3", FontFamily = Icons, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) },
                new TextBlock { Text = "Показать пример" },
            } } };
        preview.Click += (_, _) => ShowToastPreview();
        DockPanel.SetDock(preview, Dock.Right);
        row.Children.Add(preview);
        var combo = new ComboBox { Width = 190, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = d.SwitchToast, ToolTip = "Где показывать уведомление",
            ItemsSource = Enum.GetValues<ToastPlacement>().Select(SwitchToastLogic.PlacementName).ToList(), SelectedIndex = (int)d.ToastPlacement };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            SetToastSettings(placement: (ToastPlacement)combo.SelectedIndex);
            Log("Уведомление о смене профиля: " + SwitchToastLogic.PlacementName(d.ToastPlacement).ToLowerInvariant() + ".");
            ShowToastPreview();
        };
        DockPanel.SetDock(combo, Dock.Right);
        row.Children.Add(combo);
        var on = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = d.SwitchToast, VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = "Показывать уведомление при смене профиля", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap } };
        void Toggle(bool v)
        {
            if (_loading || d.SwitchToast == v) return;
            SetToastSettings(enabled: v);
            Log(v ? "Уведомление при смене профиля включено." : "Уведомление при смене профиля выключено.");
            if (!v) _toast?.HideNow();
            Dispatcher.BeginInvoke(BuildAutoSwitchCard, System.Windows.Threading.DispatcherPriority.Background);
        }
        on.Checked += (_, _) => Toggle(true);
        on.Unchecked += (_, _) => Toggle(false);
        row.Children.Add(on);
        sp.Children.Add(row);
        sp.Children.Add(T("Когда профиль включается сам — при переходе в другую программу (и обратно на профиль по умолчанию) или кнопкой на клавиатуре. Не забирает фокус, исчезает через 2 секунды, щелчок — скрыть. При выборе профиля вручную не показывается.", 12, muted: true)
            .With(t => t.Margin = new Thickness(48, 4, 0, 0)));
        var sys = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = d.ToastSystem, IsEnabled = d.SwitchToast, Margin = new Thickness(0, 8, 0, 0),
            Content = new TextBlock { Text = "Ещё и системное уведомление Windows (попадает в центр уведомлений)", TextWrapping = TextWrapping.Wrap } };
        sys.Checked += (_, _) => { if (!_loading) SetToastSettings(system: true); };
        sys.Unchecked += (_, _) => { if (!_loading) SetToastSettings(system: false); };
        sp.Children.Add(sys);
        return sp;
    }

    /// <summary>Screenshots/smoke test: the user's «1С» profile that switches on in 1cv8c.</summary>
    internal void DemoV55()
    {
        var d = _store.Data;
        if (_store.Find("1С") == null)
        {
            var p = d.Profiles[0].Clone("1С");
            p.Accent = AccentPalette[6]; p.Glyph = GlyphPalette[4]; p.Comment = "Бухгалтерия: F13 — новый документ";
            d.Profiles.Add(p);
        }
        d.AppRules.RemoveAll(r => string.Equals(r.ProcessName, "1cv8c", StringComparison.OrdinalIgnoreCase));
        d.AppRules.Add(new AppSwitchRule { ProcessName = "1cv8c", ProfileName = "1С", DisplayName = "1С:Предприятие" });
        if (d.FallbackProfile.Length == 0 && _store.Find("Основной") != null) d.FallbackProfile = "Основной";
        _autoFallback = false;
        _demoForeground = ("1cv8c", "1С:Предприятие — Бухгалтерия (учебная)");
        ActivateProfile("1С", "авто", "по программе 1cv8c.exe", auto: true);
    }
}
