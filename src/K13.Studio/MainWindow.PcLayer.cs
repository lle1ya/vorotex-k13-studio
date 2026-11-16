using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>
/// v5.6: «Кнопки по профилю (через ПК)». After a one-time «Подготовить клавиатуру» write each key runs a keypad macro «Scroll Lock + F<i>» (v5.6.3; F14–F24 did not work) and the
/// keyboard hook runs what the ACTIVE profile assigns — so a profile switch changes the keys instantly, without writes.
/// «В клавиатуре» (classic) writes the real keys again so the keypad works without the app.
/// </summary>
public partial class MainWindow
{
    private PcLayerState Layer => _store.Data.PcLayer;

    /// <summary>What the keypad must contain for the active profile in the current mode.</summary>
    internal K13DeviceConfig DeviceTarget => Layer.Active ? PcLayerPlanner.Apply(Cfg, Layer) : Cfg;

    private byte[]? _kpImageRef;
    private K13DeviceConfig? _kpCfg;
    /// <summary>The keypad contents as last read/written (decoded once per image), or null when unknown.</summary>
    private K13DeviceConfig? KeypadCfg
    {
        get
        {
            if (_deviceImage == null) return null;
            if (!ReferenceEquals(_deviceImage, _kpImageRef)) { _kpImageRef = _deviceImage; try { _kpCfg = K13MemoryCodec.Decode(_deviceImage); } catch { _kpCfg = null; } }
            return _kpCfg;
        }
    }

    private void PushMapper()
    {
        _mapper?.Update(_profile.Host, _profile.Name);
        _mapper?.UpdateLayer(Layer, Cfg, _profile.Name);
    }

    private static string KeyList(IEnumerable<int> cols) => string.Join(", ", cols.Select(DeviceView.ControlName));

    // ───────────────────────────── switching modes (both need one write, confirmation, backup, replug)
    internal async Task<bool> PrepareKeypad()
    {
        if (_offline || !_connected) { MessageBox.Show(this, "Подключите клавиатуру — подготовка записывает её один раз.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        var state = PcLayerPlanner.NewState(_store.Data.Profiles.Select(p => p.Device ?? K13MemoryCodec.Decode(DefaultImage)));
        state.PreparedAt = DateTime.Now;
        var plan = state.Keys;
        MessageBox.Build(this, PrepareText(state), "Подготовить клавиатуру", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes, out var ans,
            new[] { "Подготовить и записать", "Отмена" }).ShowDialog();
        if (ans() != MessageBoxResult.Yes) { BuildAutoSwitchCard(); return false; }
        bool ok = await WriteDevice(target: PcLayerPlanner.Apply(Cfg, state));
        if (ok)
        {
            _store.Data.PcLayer = state; SaveStore();
            Log($"Кнопки через ПК: клавиатура подготовлена ({plan.Count} кнопок).");
            StatusText.Text = "Клавиатура подготовлена: кнопки меняются вместе с профилем. Нажмите любую — внизу будет видно, что она сделала.";
        }
        PushMapper(); RefreshAll(); RefreshDirty();
        return ok;
    }

    private string PrepareText(PcLayerState state)
    {
        var plan = state.Keys;
        var native = PcLayerPlanner.Columns.Where(c => !state.IsMapped(c)).ToList();
        return
            "Кнопки будут меняться вместе с профилем — сразу, без записи в клавиатуру.\n\n" +
            "Для этого клавиатура один раз записывается так, что каждая кнопка запускает свой короткий макрос «Scroll Lock + F1…F11», " +
            "а программа узнаёт кнопку и выполняет то, что назначено ей в активном профиле: клавишу, сочетание, макрос, медиа, действие на ПК.\n\n" +
            $"• Через ПК: {KeyList(plan.Select(k => k.Column))}.\n" +
            (native.Count > 0 ? $"• Остаётся в клавиатуре (макросов в клавиатуре всего 11): {KeyList(native)}.\n" : "") +
            "• Джойстик и нажатие стика (кольцо) остаются в клавиатуре, подсветка — тоже. Все 11 макросов клавиатуры заняты этим режимом.\n" +
            "• Кнопка срабатывает как нажатие; при удержании не повторяется.\n" +
            "• Пока режим включён, Scroll Lock обычной клавиатуры не действует.\n" +
            "• Программа должна быть запущена (можно вместе с Windows, в трей): без неё кнопки нажимают Scroll Lock + F1…F11.\n\n" +
            "Перед записью сохраняется резервная копия, после — проверка чтением. Переподключать кабель не нужно.";
    }

    internal Window BuildPrepareDialog()
    {
        var state = new PcLayerState { Scheme = PcLayerState.ChordScheme, Keys = PcLayerPlanner.NewState(_store.Data.Profiles.Select(p => p.Device ?? K13MemoryCodec.Decode(DefaultImage))).Keys };
        return MessageBox.Build(IsLoaded ? this : null, PrepareText(state), "Подготовить клавиатуру", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes, out _,
            new[] { "Подготовить и записать", "Отмена" });
    }

    /// <summary>Screenshots / smoke test: the user's case — macro on Tab in «Основной», plain Tab and F2 on key 1 in «1С».</summary>
    internal void DemoV56()
    {
        var main = _store.Find("Основной"); var oneC = _store.Find("1С");
        if (main?.Device == null || oneC?.Device == null) return;
        int slot = Enumerable.Range(0, KeyCatalog.MacroCodeCount).FirstOrDefault(i => main.Device.Macros[i].IsUsed);
        main.Device.SetKey(9, (byte)(KeyCatalog.FirstMacro + slot));
        oneC.Device.SetKey(9, 0x2B);
        oneC.Device.SetKey(0, 0x3B);
        SaveStore(); RefreshAll();
    }

    /// <summary>Screenshots / smoke test: what the keypad holds (normally known only after a read or write).</summary>
    internal void DemoKeypadHolds(string? profile)
    {
        var p = profile == null ? null : _store.Find(profile);
        _deviceImage = p?.Device == null ? null : K13MemoryCodec.Encode(Layer.Active ? PcLayerPlanner.Apply(p.Device, Layer) : p.Device, DefaultImage);
        RefreshDirty(); RefreshLayout();
    }

    internal async Task<bool> RestoreKeypad()
    {
        if (_offline || !_connected)
        {
            MessageBox.Show(this, "Подключите клавиатуру: чтобы кнопки снова работали без программы, их нужно записать в клавиатуру.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            BuildAutoSwitchCard(); return false;
        }
        MessageBox.Build(this,
            $"В клавиатуру будут записаны кнопки профиля «{_profile.Name}». Они будут работать и без программы, но перестанут меняться вместе с профилем " +
            "(для другого профиля — снова «Записать»).\n\nПеред записью — резервная копия, после — проверка. Затем переподключите клавиатуру.",
            "Кнопки — в клавиатуру", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes, out var ans, new[] { "Записать в клавиатуру", "Отмена" }).ShowDialog();
        if (ans() != MessageBoxResult.Yes) { BuildAutoSwitchCard(); return false; }
        bool ok = await WriteDevice(target: Cfg);
        if (ok)
        {
            _store.Data.PcLayer = new PcLayerState(); SaveStore();
            Log("Кнопки снова записаны в клавиатуру (режим «в клавиатуре»).");
        }
        PushMapper(); RefreshAll(); RefreshDirty();
        return ok;
    }

    /// <summary>Screenshots / smoke test: pretend the keypad was prepared (no device involved).</summary>
    internal void DemoPcLayer(bool on)
    {
        _store.Data.PcLayer = on ? new PcLayerState { Scheme = PcLayerState.ChordScheme, Keys = PcLayerPlanner.NewState(_store.Data.Profiles.Select(p => p.Device ?? K13MemoryCodec.Decode(DefaultImage))).Keys, PreparedAt = DateTime.Now } : new PcLayerState();
        _kpUnexpectedVk = 0; _k13RoutedSeen = false;
        SaveStore(); PushMapper(); RefreshAll();
    }

    internal PcLayerState LayerState => Layer;
    internal string WriteNoteShown => WriteNote.Visibility == Visibility.Visible ? WriteNote.Text : "";
    internal bool ModeBannerWarns => ModeBanner.Visibility == Visibility.Visible && ModeBanner.Content is FrameworkElement f && (f.Tag as string) == "warn";

    // ───────────────────────────── «Кнопки по профилю» row in the auto-switch card
    private FrameworkElement PcModeRow()
    {
        bool on = Layer.Active;
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            ToolTip = on ? "Выключить: записать кнопки обратно в клавиатуру" : "Включить: один раз подготовить клавиатуру" };
        sw.Checked += async (_, _) => { if (!_loading && !Layer.Active) await PrepareKeypad(); };
        sw.Unchecked += async (_, _) => { if (!_loading && Layer.Active) await RestoreKeypad(); };
        var status = new TextBlock { FontSize = 12.5, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            Text = on ? $"через ПК · {RuPlural.Of(Layer.Keys.Count, "кнопка", "кнопки", "кнопок")}" : "кнопки в клавиатуре — меняются только по «Записать»" };
        return SettingRow("Кнопки по профилю", sw, status, InfoDot(PcModeHelp));
    }

    private const string PcModeHelp =
        "Через ПК: клавиатура один раз подготавливается («Подготовить клавиатуру»), и каждая кнопка запускает свой короткий макрос «Scroll Lock + F1…F11», который узнаёт программа. " +
        "Программа выполняет то, что назначено кнопке в активном профиле, — смена профиля меняет кнопки сразу, без записи. " +
        "Нужна запущенная программа (можно в трее, с запуском вместе с Windows).\n\n" +
        "В клавиатуре: кнопки записаны в саму клавиатуру и работают без программы, но для другого профиля их нужно снова записать.\n\n" +
        "В обоих режимах джойстик, нажатие стика и подсветка хранятся в клавиатуре.";

    // ───────────────────────────── the line under «Нажмите на кнопку…» on the Buttons tab
    private void RefreshModeBanner()
    {
        var b = BuildModeBanner();
        ModeBanner.Content = b;
        ModeBanner.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement? BuildModeBanner()
    {
        var kp = KeypadCfg;
        if (Layer.Active)
        {
            if (Layer.Legacy)
                return Banner(true, "Режим «через ПК» подготовлен по-старому (коды F14–F24) — эта клавиатура их не посылает. Подготовьте её заново.", "Подготовить снова", async () => await PrepareKeypad());
            if (_kpUnexpectedVk != 0)
                return Banner(true, $"Клавиатура прислала «{VkName(_kpUnexpectedVk)}» вместо своего сигнала — кнопки через ПК сейчас не работают. Нажмите «Подготовить снова» или выключите «Кнопки по профилю».", null, null);
            if (kp != null && !PcLayerPlanner.LooksPrepared(Layer, kp))
                return Banner(true, "Клавиатура не подготовлена для режима «через ПК» — кнопки сейчас делают то, что в ней записано.", "Подготовить снова", async () => await PrepareKeypad());
            var mism = kp == null ? new() : PcLayerPlanner.Compare(Cfg, Layer, kp);
            if (mism.Count > 0)
                return Banner(true, $"В клавиатуре другое на: {KeyList(mism.Select(m => m.Column).Take(4))} — нажмите «Записать».", null, null);
            return Banner(false, $"Кнопки меняются вместе с профилем (через ПК) — сейчас «{_profile.Name}».", null, null);
        }
        if (_kpForeignVk != 0)
            return _activationFailed
                ? Banner(true, $"После активации клавиатура всё ещё присылает «{VkName(_kpForeignVk)}» — заводская раскладка. Попробуйте полное чтение памяти; если не поможет — ⋯ → «Сохранить диагностику».",
                         "Прочитать память", async () => { if (await ReadDevice(auto: false) != null) { _kpForeignVk = 0; _activationFailed = false; RefreshModeBanner(); } })
                : Banner(true, $"Клавиатура прислала «{VkName(_kpForeignVk)}», а в её памяти такой клавиши нет — она работает по заводской раскладке. «Активировать» включит записанную (только чтение, без записи).",
                         "Активировать", async () => await ActivateKeypad(auto: false));
        if (kp != null)
        {
            var mism = PcLayerPlanner.Compare(Cfg, Layer, kp);
            if (mism.Count > 0)
            {
                var m0 = mism[0];
                return Banner(true, $"Клавиатура делает не то, что показано: {DeviceView.ControlName(m0.Column)} — в ней «{KpName(m0.InKeypad)}»" +
                                    (mism.Count > 1 ? $" и ещё {mism.Count - 1}" : "") + ". «Записать» или «Кнопки по профилю».", "Через ПК…", async () => await PrepareKeypad());
            }
            return null;
        }
        // keypad unknown: say it once there is more than one profile with different keys
        var profiles = _store.Data.Profiles.Where(p => p.Device != null).ToList();
        bool differ = profiles.Count > 1 && PcLayerPlanner.Columns.Any(c => profiles.Select(p => p.Device!.GetKey(c)).Distinct().Count() > 1);
        return differ ? Banner(false, "Кнопки записаны в клавиатуре и не меняются вместе с профилем.", "Сделать через ПК…", async () => await PrepareKeypad()) : null;
    }

    private FrameworkElement Banner(bool warn, string text, string? action, Func<Task>? onAction)
    {
        var tagged = BannerCore(warn, text, action, onAction); tagged.Tag = warn ? "warn" : "info"; return tagged;
    }

    private FrameworkElement BannerCore(bool warn, string text, string? action, Func<Task>? onAction)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = warn ? "\uE7BA" : "\uE946", FontFamily = Icons, FontSize = 12, Foreground = warn ? R("WarnBrush") : R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = warn ? R("TextBrush") : R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 });
        if (action != null && onAction != null)
        {
            var a = new Button { Style = (Style)FindResource("LinkButton"), Content = action, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
            a.Click += async (_, _) => await onAction();
            row.Children.Add(a);
        }
        return new Border { Child = row, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6, 12, 6), Background = warn ? R("AccentSoftBrush") : Brushes.Transparent };
    }

    /// <summary>Under «Что делает …?»: whether this key follows the profile and, if the keypad differs, what it really does.</summary>
    private FrameworkElement? KeyModeNote(int col)
    {
        if (!IsKeyCol(col)) return null;
        var kp = KeypadCfg;
        bool key = PcLayerPlanner.Columns.Contains(col);
        string? muted = null, warn = null;
        if (Layer.Active && Layer.IsMapped(col))
        {
            var a = PcLayerPlanner.Resolve(Cfg, col);
            muted = a.Kind == PcKeyKind.Macro ? $"Через ПК: макрос выполняет компьютер, пока программа запущена. В другом профиле кнопка делает своё."
                                              : $"Через ПК: в профиле «{_profile.Name}» — {PcLayerPlanner.Describe(a)}. В другом профиле кнопка делает своё.";
            if (a.Kind == PcKeyKind.FirmwareOnly) warn = $"«{KeyCatalog.Name(a.Code)}» умеет только сама клавиатура — в режиме «через ПК» эта кнопка ничего не сделает.";
            if (kp != null && kp.GetKey(col) != PcLayerPlanner.CellFor(Layer, Layer.Keys.First(k => k.Column == col))) warn = $"Клавиатура не подготовлена: сейчас эта кнопка посылает «{KpName(kp.GetKey(col))}».";
        }
        else
        {
            if (Layer.Active) muted = key ? "Эта кнопка записана в клавиатуре и не меняется вместе с профилем." : "Джойстик и нажатие стика записаны в клавиатуре.";
            else if (KeyCatalog.IsMacro(Cfg.GetKey(col))) muted = "Макрос записан в клавиатуру и работает без программы; в другом профиле — только после «Записать».";
            if (kp != null && kp.GetKey(col) != DeviceTarget.GetKey(col))
                warn = $"Сейчас в клавиатуре: «{KpName(kp.GetKey(col))}». Нажмите «Записать», чтобы было как здесь" + (Layer.Active || !key ? "." : ", или включите «Кнопки по профилю».");
        }
        _keyModeHelp = muted; // v5.7: the calm explanation goes into «?» next to the title; only warnings stay on screen
        if (warn == null) return null;
        muted = null;
        var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        if (warn != null)
        {
            var w = new DockPanel { Margin = new Thickness(0, muted != null ? 6 : 0, 0, 0) };
            w.Children.Add(new TextBlock { Text = "\uE7BA", FontFamily = Icons, FontSize = 12, Foreground = R("WarnBrush"), Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Top });
            w.Children.Add(new TextBlock { Text = warn, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(new Border { Child = w, Background = R("AccentSoftBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 7) });
        }
        return sp;
    }

    private string? _keyModeHelp;

    /// <summary>Name of a code read from the keypad; macros are numbered from 1 like the macro cells, not by catalog index.</summary>
    private static string KpName(byte c) => KeyCatalog.IsMacro(c) ? $"макрос из ячейки {KeyCatalog.MacroSlotOf(c) + 1}" : KeyCatalog.Name(c);

    // ───────────────────────────── v5.6.2: is a write needed? what does the keypad really send?
    private K13KeyMonitor? _k13Keys;
    private ushort _kpUnexpectedVk;
    /// <summary>v5.7.3: a key the keypad sent that its memory (last read / written image) cannot produce.</summary>
    private ushort _kpForeignVk;
    private int _ringLogged;

    private void CheckForeignKey(ushort vk)
    {
        if (_bisecting || _kpForeignVk != 0 || KeypadCfg is not { } kp || KeypadOutputCheck.Explains(kp, vk)) return;
        _kpForeignVk = vk;
        if (_activatedAt > DateTime.MinValue && (DateTime.Now - _activatedAt).TotalMinutes < 10) _activationFailed = true;
        Log($"[k13] ВНИМАНИЕ: клавиатура прислала «{VkName(vk)}» (VK 0x{vk:X2}), но в её памяти (последнее чтение/запись) такой клавиши нет ни в кнопках, ни в макросах. " +
            $"Похоже, прошивка работает по заводской раскладке. Заголовок памяти: {HeaderHex(_deviceImage)}.");
        StatusText.Text = $"Клавиатура прислала «{VkName(vk)}» — такого нет в её памяти (заводская раскладка?).";
        RefreshModeBanner();
    }

    private static string HeaderHex(byte[]? img) =>
        img is { Length: >= K13Layout.HeaderAddress + 8 } ? Convert.ToHexString(img, K13Layout.HeaderAddress, 8) : "неизвестен";
    private int _k13Logged;
    private bool _k13RoutedSeen;

    /// <summary>Text next to «Записать». In «через ПК» mode most edits need no write — say so instead of a silently disabled button.</summary>
    internal string WriteNoteText(bool dirty)
    {
        if (!Layer.Active) return "";
        if (!dirty) return "Запись не нужна — работает через ПК";
        var kp = _deviceImage;
        if (kp == null) return "";
        try
        {
            var what = PcLayerHealth.PendingWrite(DeviceTarget, kp).Select(b => b.Purpose).Distinct().ToList();
            return what.Count == 0 ? "Запись не нужна — работает через ПК" : "Нужно записать: " + string.Join(", ", what.Select(w => w.ToLowerInvariant()));
        }
        catch { return ""; }
    }

    private void RefreshWriteNote(bool dirty)
    {
        if (WriteNote == null) return;
        string t = WriteNoteText(dirty);
        WriteNote.Text = t;
        WriteNote.Visibility = t.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        WriteNote.ToolTip = t.StartsWith("Запись не нужна")
            ? "Кнопки «через ПК» берут действие из активного профиля сразу — в клавиатуру ничего записывать не надо.\nЗапись нужна только для подсветки, джойстика, нажатия стика и кнопки, оставшейся в клавиатуре."
            : null;
    }

    internal void OnLayerRouted(int col, ushort vk, string profile, PcKeyAction? a)
    {
        if (!_k13RoutedSeen) { _k13RoutedSeen = true; _kpUnexpectedVk = 0; RefreshModeBanner(); }
        if (_exiting) return;
        string what = a == null ? "—" : a.Kind switch
        {
            PcKeyKind.Macro => $"макрос «{MacroTitle(a.MacroSlot)}»",
            PcKeyKind.None => "ничего (кнопка отключена в этом профиле)",
            _ => KeyCatalog.Name(a.Code),
        };
        StatusText.Text = $"{char.ToUpperInvariant(DeviceView.ControlName(col)[0])}{DeviceView.ControlName(col)[1..]} → {what} (профиль «{profile}», через ПК).";
    }

    private string MacroTitle(int slot) => _profile.MacroNames.TryGetValue(slot, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"Макрос {slot + 1}";

    /// <summary>A key event that physically came from the K13 and was NOT taken by the PC layer.</summary>
    internal void OnKeypadKey(ushort vk, ushort scan, bool down)
    {
        if (!down) return;
        if (_k13Logged < 300) { _k13Logged++; Log($"[k13] клавиатура прислала VK 0x{vk:X2} (скан 0x{scan:X2}) «{VkName(vk)}»" + (Layer.Active ? " — мимо режима «через ПК»" : "")); }
        if (_bisectKeys != null) _bisectKeys.Add(vk);
        if (_ringOverlay?.IsOpen == true && _ringLogged < 60) { _ringLogged++; Log($"[ring] при открытом кольце клавиатура прислала «{VkName(vk)}» (VK 0x{vk:X2})" + (_ringOverlay.WantsKey(vk) ? " — наклон/выбор" : " — не наклон стика")); }
        if (!Layer.Active) { CheckForeignKey(vk); return; }
        var h = PcLayerHealth.Check(Layer, KeypadCfg, vk);
        if (h == PcHealth.Unexpected && _kpUnexpectedVk != vk)
        {
            _kpUnexpectedVk = vk;
            Log($"[k13] режим «через ПК» не действует: кнопка клавиатуры прислала «{VkName(vk)}» вместо своего сигнала (Scroll Lock + F1…F11).");
            StatusText.Text = $"Клавиатура прислала «{VkName(vk)}» вместо своего сигнала — подготовка в ней не действует.";
            RefreshModeBanner();
        }
    }

    private static string VkName(ushort vk)
    {
        var c = KeyCatalog.All.FirstOrDefault(k => k.Vk == vk);
        return c != null ? c.Name : vk switch { 0x09 => "Tab", 0x0D => "Enter", _ => $"VK 0x{vk:X2}" };
    }
}
