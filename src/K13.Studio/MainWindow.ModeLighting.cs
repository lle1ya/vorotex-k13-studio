using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>1.3.0: different lighting for Normal and Game (the app rewrites the one lighting block on a mode change) and the
/// EXPERIMENTAL switch for the blue mode indicator (factory flag 0x000F bit 4).</summary>
public partial class MainWindow
{
    private readonly ModeLightingGate _modeLedGate = new();
    private System.Windows.Threading.DispatcherTimer? _modeLedTimer;
    private bool _modeLedCapLogged;

    internal bool ModeLightingOn => ModeLighting.Enabled(_store.Data.NormalLighting);

    /// <summary>The lighting the keypad should have right now when it differs from the profile (Normal + the setting), else null.</summary>
    private LedSettings? ModeLed() =>
        _connected && _hwMode == HwMode.Normal && ModeLightingOn ? ModeLighting.NormalLed(_store.Data.NormalLighting, _store.Data.Profiles, KeypadCfg?.Led ?? Cfg.Led) : null;

    /// <summary>What the keypad should contain: <see cref="DeviceTarget"/>, with the Normal lighting while the keypad is in Normal.</summary>
    internal K13DeviceConfig KeypadTarget => ModeLed() is { } led ? ModeLighting.WithLed(DeviceTarget, led, _deviceImage) : DeviceTarget;

    /// <summary>Called by <see cref="SetHwMode"/> on a real change (Normal ↔ Game seen in the status byte).</summary>
    private void OnHwModeChanged(HwMode from, HwMode to)
    {
        if (!ModeLightingOn || to == HwMode.Unknown) return;
        _modeLedTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ModeLighting.SettleMs) };
        _modeLedTimer.Tick -= ModeLedTimer_Tick; _modeLedTimer.Tick += ModeLedTimer_Tick;
        _modeLedTimer.Stop(); _modeLedTimer.Start();
        Log($"[mode-led] режим {K13Mode.Name(from)} → {K13Mode.Name(to)}: подсветка для {K13Mode.Name(to)} — {(to == HwMode.Normal ? ModeLighting.Describe(_store.Data.NormalLighting) : $"профиль «{_profile.Name}»")}; применю через {ModeLighting.SettleMs / 1000.0:0.#} с.");
    }

    private async void ModeLedTimer_Tick(object? sender, EventArgs e)
    {
        _modeLedTimer!.Stop();
        await ApplyModeLighting();
    }

    /// <summary>The lighting-only write for the current mode (same confirmed live path as a profile switch; BASE+0 kept as read).</summary>
    internal async Task<bool> ApplyModeLighting()
    {
        if (!ModeLightingOn || _offline || !_connected) return false;
        if (_busy) { _modeLedTimer?.Start(); return false; }
        if (!_store.Data.WriteConfirmed) { Log("[mode-led] запись в клавиатуру ещё не подтверждена (первое «Записать») — подсветку по режиму не меняю."); return false; }
        if (ModelBlocksQuiet() || FactoryBlocksQuiet()) return false;   // 1.3.4 / 1.3.1
        if (LightingTarget() is not { } t) { App.Log("[mode-led] подсветка в клавиатуре уже нужная — не пишу."); return false; }
        long now = Environment.TickCount64;
        if (!_modeLedGate.CanWrite(now))
        {
            if (!_modeLedCapLogged) { _modeLedCapLogged = true; Log($"[mode-led] уже {ModeLighting.MaxPerHour} смен подсветки по режиму за час — больше не пишу в этот час (бережём память клавиатуры)."); }
            return false;
        }
        _modeLedGate.Wrote(now); _modeLedCapLogged = false;
        Log($"[mode-led] режим {K13Mode.Name(_hwMode)}: пишу только подсветку ({_modeLedGate.WritesLastHour(now)}-я смена за час; флаги BASE+0 — как в клавиатуре).");
        return await WriteDevice(quiet: true, target: t, keepModeFlags: true);
    }

    internal void SetNormalLighting(string choice)
    {
        _store.Data.NormalLighting = choice ?? "";
        SaveStore();
        Log($"Подсветка в режиме Normal: {ModeLighting.Describe(choice)}.");
        RefreshDirty(); RefreshModeLightPanel();
        if (_connected && !_offline && _store.Data.WriteConfirmed && LightingPending()) ScheduleLiveLighting();
    }

    internal const string ModeLightExplain =
        "У K13 одна подсветка на оба режима: стик переключает только назначения кнопок. Программа сама меняет подсветку, когда видит смену режима " +
        "(опрос клавиатуры раз в 2 с — подсветка сменится через 2–4 с после нажатия стика). Работает, пока программа запущена (можно в трее). " +
        "Каждая смена — короткая запись блока подсветки (как при смене профиля), не больше " + "40 раз в час.";

    private void RefreshModeLightPanel()
    {
        if (ModeLightPanel == null) return;
        ModeLightPanel.Children.Clear();
        ModeLightPanel.Children.Add(T("Подсветка в режиме Normal", 14, semi: true).With(t => t.Margin = new Thickness(0, 8, 0, 4)));
        var box = new ComboBox { MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        var items = new List<(string Value, string Text)> { ("", "Как в Game (одна подсветка)"), (ModeLighting.Off, "Выключена") };
        items.AddRange(_store.Data.Profiles.Select(p => (p.Name, $"Как в профиле «{p.Name}»")));
        string cur = _store.Data.NormalLighting ?? "";
        if (cur.Length > 0 && !items.Any(i => i.Value == cur)) items.Add((cur, $"Профиль «{cur}» (удалён)"));
        foreach (var (v, text) in items) box.Items.Add(new ComboBoxItem { Content = text, Tag = v });
        box.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Value == cur));
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string v } && v != (_store.Data.NormalLighting ?? "")) SetNormalLighting(v); };
        ModeLightPanel.Children.Add(box);
        string state = !_connected ? "" : _hwMode switch
        {
            HwMode.Normal => " Сейчас клавиатура в Normal.",
            HwMode.Game => " Сейчас клавиатура в Game — горит подсветка профиля.",
            _ => "",
        };
        ModeLightPanel.Children.Add(T((ModeLightingOn ? "В Game — подсветка активного профиля (эта страница), в Normal — выбранная здесь. " : "") + ModeLightExplain + state, 12, muted: true)
            .With(t => t.TextWrapping = TextWrapping.Wrap));
        var ind = new Button { Style = (Style)FindResource("LinkButton"), Content = "Синий индикатор между Tab и 9…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), FontSize = 13 };
        ind.Click += (_, _) => ShowIndicatorDialog();
        ModeLightPanel.Children.Add(ind);
    }

    // ───────────────────────────── blue mode indicator (0x000F bit 4) — EXPERIMENTAL
    internal string IndicatorMenuText()
    {
        var (v, fresh) = RememberState;
        string st = v is not byte b ? "?" : (K13ModeIndicator.IsOn(b) ? "вкл" : "выкл") + (fresh ? "" : " (по последнему чтению)");
        return $"Синий индикатор режима: {st}… (эксперимент)";
    }

    internal const string IndicatorExplain =
        "Синий светодиод между Tab и 9 показывает режим Game. Он не входит в RGB-подсветку: яркость у него не настраивается — ни в программе производителя, " +
        "ни в заводской программе прошивки (IST83025B) такой настройки нет.\n\n" +
        "Включает его заводской флаг MODE_LED_EN (ячейка 0x000F, бит 4; на K13 сейчас 0x91). Если бит снять (0x81), индикатор, по описанию заводской программы, " +
        "станет индикатором Win Lock — а такой функции у K13 нет, поэтому он должен перестать загораться. ЭТО НЕ ПРОВЕРЕНО на клавиатуре: возможно, понадобится " +
        "переподключение USB, возможно, прошивка поведёт себя иначе. Тогда включите обратно здесь же или восстановите ячейку из резервной копии.\n\n" +
        "Режим без индикатора остаётся виден в программе (строка состояния) и по кнопкам. Запись: полная резервная копия, один байт (только бит 4), проверка чтением. " +
        "Байты 0x0000–0x0007 программа не записывает никогда.";

    internal Window IndicatorDialog(byte? cur, bool canWrite, out Func<MessageBoxResult> answer)
    {
        bool on = cur is not byte b0 || K13ModeIndicator.IsOn(b0);
        string state = cur is byte b ? $"Сейчас: индикатор {(K13ModeIndicator.IsOn(b) ? "ВКЛЮЧЁН" : "ВЫКЛЮЧЕН")} (0x000F = 0x{b:X2})." : "Сейчас: неизвестно (клавиатура не прочитана).";
        string text = state + "\n\n" + IndicatorExplain + (canWrite ? "" : "\n\nПодключите клавиатуру, чтобы изменить.");
        return MessageBox.Build(IsLoaded ? this : null, text, "Синий индикатор режима (эксперимент)",
            canWrite ? MessageBoxButton.YesNo : MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.No, out answer,
            canWrite ? new[] { on ? "Выключить индикатор…" : "Включить индикатор…", "Закрыть" } : null);
    }

    internal async void ShowIndicatorDialog()
    {
        bool canWrite = _connected && !_busy && !_offline;
        if (canWrite)
        {
            try
            {
                var blk = await Task.Run(() => { using var d = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена."); return d.ReadBlocks(new[] { (K13RememberMode.BlockAddress, 8) })[0]; });
                _rememberByte = blk[7]; _rememberFresh = true;
                Log($"[indicator] прочитано 0x0008–0x000F = {Convert.ToHexString(blk)}: индикатор {K13ModeIndicator.Describe(_rememberByte)}.");
            }
            catch (Exception ex) { Log("[indicator] чтение 0x000F не удалось: " + ex.Message); canWrite = false; }
        }
        if (canWrite && _modelCheck != ModelCheck.K13) { canWrite = false; Log("[indicator] запись недоступна: " + ModelCheckText() + "."); }   // 1.3.4
        var cur = canWrite ? _rememberByte : RememberState.Value;
        if (ConnectedModel is KeypadModel.K12Likely or KeypadModel.Unknown) canWrite = false;
        IndicatorDialog(cur, canWrite && cur != null, out var answer).ShowDialog();
        if (answer() != MessageBoxResult.Yes || cur is not byte c) return;
        bool turnOn = !K13ModeIndicator.IsOn(c);
        var (t, err) = K13ModeIndicator.Target(c, turnOn);
        if (t is not byte target) { MessageBox.Show(this, err ?? "Отменено.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.ShowCustom(this,
                $"{(turnOn ? "Включить" : "Выключить")} синий индикатор режима?\n\n" +
                $"• Ячейка 0x000F: 0x{c:X2} → 0x{target:X2} (меняется только бит 4, один байт).\n" +
                "• Сначала программа сохранит полную резервную копию памяти, после записи — прочитает ячейку и сверит.\n" +
                "• Это эксперимент: результат проверьте после переподключения USB.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, "Записать", "Отмена") != MessageBoxResult.Yes)
        { Log("[indicator] запись отменена пользователем."); return; }
        _busy = true; UpdateConn();
        StatusText.Text = "Резервная копия и запись ячейки 0x000F…";
        try
        {
            var r = await Task.Run(() =>
            {
                using var dev = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена.");
                K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
                try
                {
                    return K13RememberMode.Run(dev, x => K13ModeIndicator.Target(x, turnOn), m => App.Log(m.Replace("[remember]", "[indicator]")),
                        allowedBits: K13ModeIndicator.Bit, done: K13ModeIndicator.Done, what: "«Синий индикатор режима»", backupTag: "k13-indicator-before");
                }
                finally { K13WriteGate.Lock(); }
            });
            Log($"[indicator] {r.Message} (было 0x{r.Before:X2}, стало 0x{r.After:X2}; резервная копия: {r.BackupPath})");
            _rememberByte = r.After; _rememberFresh = true;
            if (r.Written && _deviceImage is { Length: > K13RememberMode.Address } img) img[K13RememberMode.Address] = r.After;
            StatusText.Text = r.Message;
            MessageBox.Show(this, r.Message + $"\n\nРезервная копия до изменения:\n{r.BackupPath}", Title, MessageBoxButton.OK, r.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("[indicator] ошибка: " + ex.Message);
            MessageBox.Show(this, "Не удалось: " + FriendlyError.Text(ex), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; UpdateConn(); }
    }

}
