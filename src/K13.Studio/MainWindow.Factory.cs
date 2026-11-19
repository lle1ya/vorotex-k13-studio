using System.Windows;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>1.2.0 «Вернуть заводские назначения…» (separate from «Вернуть обычный режим», which only ends the HID session).
/// No native factory-reset command is known for the K13, so this is an ordinary confirmed write of the parts whose factory state
/// is known (K13FactoryAssignments): read + backup first, then the usual «Записать» path (its own backup, changed blocks only,
/// read-back, automatic restore on mismatch). Lighting and the service area are not touched. PROTOCOL §41.</summary>
public partial class MainWindow
{
    /// <summary>1.2.0 created a profile with this name after the reset; 1.3.1 no longer does (existing ones stay — user data).</summary>
    internal const string FactoryProfileName = "Заводские назначения";

    internal string FactoryDialogText(K13FactoryAssignments.Plan plan) =>
        "Вернуть кнопкам, колесу и макросам клавиатуры заводские назначения?\n\n" +
        "Отдельной команды «сброс до заводских» у K13 нет, поэтому программа записывает то, что известно точно:\n" +
        string.Join("\n", plan.Resets.Select(x => "• " + x)) + "\n\n" +
        "Не меняется:\n" + string.Join("\n", plan.Keeps.Select(x => "• " + x)) + "\n\n" +
        (Layer.Active ? "Сейчас включены «Кнопки по профилю (через ПК)» — режим будет выключен: заводские кнопки работают сами, без программы.\n\n" : "") +
        "Сначала память клавиатуры будет прочитана и сохранена в резервную копию (вернуть: «⋯ → Восстановить из резервной копии»). " +
        "Затем запись — только изменённых блоков, с проверкой чтением; если что-то не совпадёт, память вернётся как было.";

    internal Window BuildFactoryDialog(out Func<MessageBoxResult> answer)
    {
        var plan = K13FactoryAssignments.Build(KeypadCfg ?? Cfg, _deviceImage);
        return MessageBox.Build(IsLoaded ? this : null, FactoryDialogText(plan), "Вернуть заводские назначения", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No, out answer, new[] { "Сохранить копию и вернуть", "Отмена" });
    }

    internal async Task<bool> ResetToFactory()
    {
        if (_offline || !_connected || _busy) { MessageBox.Show(this, "Подключите клавиатуру: заводские назначения записываются в неё.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        if (_imageFresh && !K13Model.WriteAllowed(K13Model.Detect(_deviceImage)))
        { MessageBox.Show(this, K13Model.Refusal(K13Model.Detect(_deviceImage)), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        BuildFactoryDialog(out var ans).ShowDialog();
        if (ans() != MessageBoxResult.Yes) return false;
        // 1. read + backup (read-only frames)
        var img = await ReadDevice(auto: false, prefix: "k13-before-factory");
        if (img == null) { MessageBox.Show(this, "Не удалось прочитать клавиатуру — ничего не записано.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        var model = K13Model.Detect(img);
        if (!K13Model.WriteAllowed(model)) { NoteModel(img); MessageBox.Show(this, K13Model.Refusal(model), Title, MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        K13DeviceConfig current;
        try { current = K13MemoryCodec.Decode(img); }
        catch (Exception ex) { MessageBox.Show(this, "Память прочитана и сохранена, но разобрать её не удалось: " + FriendlyError.Text(ex) + "\nНичего не записано.", Title); return false; }
        var plan = K13FactoryAssignments.Build(current, img);
        Log($"[factory] заводские назначения: кнопки {(plan.ChangedColumns.Count == 0 ? "уже заводские" : "меняются: " + string.Join(", ", plan.ChangedColumns.Select(DeviceView.ControlName)))}; " +
            $"колесо {current.WheelFirst}/{current.WheelSecond} → 0/1; макросов было {current.Macros.Count(m => m.IsUsed)}; подсветка не меняется.");
        // 2. the usual confirmed write (own backup, changed blocks, read-back, restore on mismatch)
        bool ok = await WriteDeviceFactory(plan.Target);
        if (!ok) { Log("[factory] запись не выполнена — профили не изменены."); return false; }
        if (Layer.Active) { _store.Data.PcLayer = new PcLayerState(); Log("[factory] «Кнопки по профилю (через ПК)» выключены."); }
        AfterFactoryWrite();
        return true;
    }

    /// <summary>1.3.1: no separate profile any more (1.2.0 created «Заводские назначения»; existing ones are left alone). The keypad
    /// shows the factory state, the PC profiles stay as they are and are written only by «Применить профиль».</summary>
    internal void AfterFactoryWrite()
    {
        _store.Data.KeypadFactory = true; _factoryAutoLogged = _factoryQuietLogged = false;
        _autoLedTimer?.Stop(); _ledTimer?.Stop(); _modeLedTimer?.Stop();
        SaveStore();
        PushMapper(); RefreshAll(); RefreshDirty(); UpdateConn();
        Log($"[factory] {FactoryStateText}. Профиль «{_profile.Name}» и остальные профили не изменены и в клавиатуру не пишутся до «Применить профиль».");
        StatusText.Text = $"Заводские назначения записаны и проверены. {FactoryStateText}. Профиль «{_profile.Name}» остался на ПК — чтобы записать его, нажмите «Применить профиль».";
    }

    // ───────────────────────────── 1.2.0: which keypad is connected (K13 / K12 share VID/PID)
    private KeypadModel _modelNoted = KeypadModel.NotRead;

    internal void NoteModel(byte[]? img)
    {
        ModelFromImage(img);   // 1.3.4
        var m = K13Model.Detect(img);
        if (m == KeypadModel.NotRead || m == _modelNoted) return;
        _modelNoted = m;
        if (m == KeypadModel.K13) { Log("[model] модель: VOROTEX K13 (заводская таблица 0x0020 совпадает)."); RefreshModeBanner(); return; }
        Log($"[model] {K13Model.Name(m)}: заводская таблица 0x0020 = {K13Model.FactoryRowHex(img)}, блок 0x0008 = {(img != null && img.Length >= 16 ? Convert.ToHexString(img, 8, 8) : "—")}. Запись заблокирована.");
        StatusText.Text = K13Model.Banner(m);
        RefreshModeBanner();
    }

    internal KeypadModel ConnectedModel => _imageFresh ? K13Model.Detect(_deviceImage) : KeypadModel.NotRead;
}
