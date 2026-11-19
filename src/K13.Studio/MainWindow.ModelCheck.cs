using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>1.3.4 (owner's approval 19.11.2026): a READ-ONLY model check on every connect (and at start with the keypad present).
/// The K13 and the K12 have the same USB ids, so after a replug Studio cannot know which one is there; until 1.3.3 it reused the
/// last K13 image and planned automatic lighting writes for a K12 (diagnostics 19.11 09:40). Now it reads the three signature
/// blocks of the built-in table (0x0048, 0x0080, 0x00A0 — 24 bytes; 07 18 00 / 05 / 01, then since 1.3.5 the full session end
/// 07 18 11 / 07 18 00 / 07 20 — without it 1.3.4 left both keypads silent) before
/// anything automatic. Exception to the v4.5 «no automatic read on connect» rule (PROTOCOL §46).
/// Until it says K13: no cached K13 image, no automatic writes (live/auto/mode lighting), no replug question.</summary>
public partial class MainWindow
{
    internal enum ModelCheck { NotChecked, K13, K12, Unknown, Failed }
    private ModelCheck _modelCheck = ModelCheck.NotChecked;
    private int _modelCheckTries;
    private bool _modelQuietLogged;
    internal ModelCheck ConnectModelCheck => _modelCheck;

    /// <summary>Automatic (quiet) writes need a keypad verified as K13 in this connection.</summary>
    internal bool ModelBlocksAuto => _connected && _modelCheck != ModelCheck.K13;

    private bool ModelBlocksQuiet()
    {
        if (!ModelBlocksAuto) return false;
        if (!_modelQuietLogged)
        {
            _modelQuietLogged = true;
            Log($"[model] автоматическая запись (подсветка) не выполняется: {ModelCheckText()}.");
        }
        return true;
    }

    internal string ModelCheckText() => _modelCheck switch
    {
        ModelCheck.K13 => "VOROTEX K13",
        ModelCheck.K12 => "подключена VOROTEX K12 — только просмотр",
        ModelCheck.Unknown => "подключена клавиатура с незнакомой заводской таблицей — только просмотр",
        ModelCheck.Failed => "модель не проверена (чтение не удалось)",
        _ => "модель проверяется",
    };

    /// <summary>Header suffix after «Клавиатура подключена».</summary>
    private string ModelSuffix() => !_connected ? "" : _modelCheck switch
    {
        ModelCheck.K13 => " · K13",
        ModelCheck.K12 => " · K12 (только просмотр)",
        ModelCheck.Unknown => " · незнакомая модель (только просмотр)",
        ModelCheck.Failed => " · модель не проверена",
        _ => "",
    };

    /// <summary>Reads the 3 signature blocks (24 bytes). Runs with _busy set so mode polling / writes never interleave HID frames.</summary>
    private async Task CheckModelOnConnect(string why)
    {
        if (_offline || !_connected || _busy || _bisecting) return;
        _busy = true;
        KeypadModel? m = null;
        string? err = null;
        try
        {
            var (model, blocks, frames) = await Task.Run(() =>
            {
                using var d = K13Device.OpenFirst(500) ?? throw new System.IO.IOException("клавиатура не открылась");
                var r = K13Model.ReadSignature(d);   // 07 18 00 / 05 / 01, then ALWAYS 07 18 11 / 07 18 00 / 07 20
                return (r.Model, r.Blocks, d.SentLog.Count);
            });
            m = model;
            App.Log($"[model] прочитано 24 байта ({frames} кадров: чтение 07 18 00/05/01 и завершение сеанса 07 18 11 / 07 18 00 / 07 20): " + string.Join(" ", blocks.Select(b => Convert.ToHexString(b))));
        }
        catch (Exception ex) { err = ex.Message; }
        finally { _busy = false; }
        _modelCheckTries++;
        ApplyModelCheck(m, why, err);
    }

    /// <summary>Applies the result (null = the read failed). Testable without a keypad.</summary>
    internal void ApplyModelCheck(KeypadModel? m, string why, string? error = null)
    {
        var was = _modelCheck;
        _modelCheck = m switch
        {
            KeypadModel.K13 => ModelCheck.K13,
            KeypadModel.K12Likely => ModelCheck.K12,
            null => ModelCheck.Failed,
            _ => ModelCheck.Unknown,
        };
        _modelQuietLogged = false;
        if (_modelCheck == ModelCheck.K13)
        {
            _modelNoted = KeypadModel.K13;
            // the cached image must be a K13 one; a K12 / unknown image from an earlier connection is dropped
            if (_deviceImage != null && K13Model.Detect(_deviceImage) != KeypadModel.K13) { _deviceImage = null; _imageFresh = false; }
            if (_deviceImage == null && LoadLastKnownImage() is { } last)
            {
                _deviceImage = last.Image; _imageFresh = false;
                Log($"Память клавиатуры целиком не читается автоматически. Для сравнения взят последний известный образ K13: {last.File}");
            }
            Log($"[model] {why}: VOROTEX K13 (проверено чтением 24 байт заводской таблицы).");
        }
        else
        {
            // never reuse a K13 image for a keypad that is not (or not yet known to be) a K13
            _deviceImage = null; _imageFresh = false;
            _autoLed.Cancel(); _autoLedTimer?.Stop(); _ledTimer?.Stop(); _modeLedTimer?.Stop();
            if (_modelCheck == ModelCheck.K12) _modelNoted = KeypadModel.K12Likely;
            else if (_modelCheck == ModelCheck.Unknown) _modelNoted = KeypadModel.Unknown;
            Log(_modelCheck == ModelCheck.Failed
                ? $"[model] {why}: проверить модель не удалось ({error}). Автоматическая запись выключена до успешного чтения («Прочитать»)."
                : $"[model] {why}: {ModelCheckText()}. Запись заблокирована, автоматическая запись выключена.");
            StatusText.Text = _modelCheck == ModelCheck.Failed
                ? "Модель клавиатуры не проверена — автоматическая запись выключена. Нажмите «Прочитать»."
                : K13Model.Banner(_modelNoted);
        }
        if (was != _modelCheck) TesterModeText();
        RefreshDirty(); UpdateConn(); RefreshModeBanner();
    }

    /// <summary>A full read / verified write tells the model too («Прочитать» after a failed check unblocks automatic writes).</summary>
    private void ModelFromImage(byte[]? img)
    {
        if (!_connected) return;
        var m = K13Model.Detect(img);
        if (m == KeypadModel.NotRead) return;
        var c = m switch { KeypadModel.K13 => ModelCheck.K13, KeypadModel.K12Likely => ModelCheck.K12, _ => ModelCheck.Unknown };
        if (c == _modelCheck) return;
        _modelCheck = c; _modelQuietLogged = false;
        Log($"[model] по прочитанной памяти: {ModelCheckText()}.");
        UpdateConn();
    }

    /// <summary>1.3.5: a session was left without its end (failed end, interrupted read) — send 07 18 11 / 07 18 00 / 07 20 again.</summary>
    private async Task RecoverOpenSession(string why)
    {
        if (_offline || !_connected || _busy || _bisecting) return;
        _busy = true;
        try
        {
            await Task.Run(() => { using var d = K13Device.OpenFirst(500) ?? throw new System.IO.IOException("клавиатура не открылась"); d.EndSession(); });
            Log($"[session] {why}: сеанс обмена остался незавершённым — отправлено завершение (07 18 11 / 07 18 00 / 07 20).");
        }
        catch (Exception ex) { App.Log($"[session] {why}: завершение сеанса не удалось: {ex.Message}"); }
        finally { _busy = false; }
        await StatusAfterSession("после завершения сеанса");
    }

    /// <summary>Status (GetFeature only) after a session end: 07 18 11 reloads the settings, the mode may differ.</summary>
    private async Task StatusAfterSession(string why)
    {
        if (_offline || !_connected || _busy) return;
        try { var st = await Task.Run(() => { using var d = K13Device.OpenFirst(500); return d?.GetStatus(); }); if (st != null) SetHwMode(st, why); }
        catch (Exception ex) { App.Log("[mode] статус " + why + ": " + ex.Message); }
    }

    private void ResetModelCheck() { _modelCheck = ModelCheck.NotChecked; _modelCheckTries = 0; _modelQuietLogged = false; }

    // smoke
    internal bool DemoHasImage => _deviceImage != null;
    internal void DemoModelCheck(KeypadModel? m) { _connected = true; ApplyModelCheck(m, "проверка", m == null ? "нет ответа" : null); }
}
