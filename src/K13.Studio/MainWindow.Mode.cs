using System.IO;
using System.Windows;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.8: hardware mode Normal (factory table) / Game (your assignments), read from the status block (GetFeature only).</summary>
public partial class MainWindow
{
    private HwMode _hwMode = HwMode.Unknown;
    private int _modeTick;
    private bool _modeBusy;
    /// <summary>v5.9.2: the keypad reported Game at connect before the app sent anything (remember-mode test).</summary>
    private bool _selfGame;
    private int _connectsSeen;

    private void NoteConnectMode()
    {
        bool replug = ++_connectsSeen > 1;
        var (v, fresh) = RememberState;
        string flag = $"0x000F: {K13RememberMode.Describe(v)}{(v != null && !fresh ? ", по последнему известному образу" : "")}";
        bool remember = v is byte rb && K13RememberMode.IsOn(rb);
        _selfGame = _hwMode == HwMode.Game && replug;
        if (_hwMode == HwMode.Game)
            Log(!replug ? $"[mode] при запуске программы клавиатура уже в Game — программа ничего не отправляла; Game могли включить раньше ({flag})."
                : remember ? $"[mode] клавиатура сама стартовала в Game — запомнила последний режим (запоминание включено; {flag})."
                : $"[mode] клавиатура сама стартовала в Game — программа ничего не отправляла ({flag}).");
        else if (_hwMode == HwMode.Normal)
            Log(remember ? $"[mode] клавиатура стартовала в Normal — это последний выбранный стиком режим (запоминание включено; {flag})."
                         : $"[mode] клавиатура стартовала в Normal ({flag}).");
        UpdateConn();
    }

    internal HwMode CurrentHwMode => _hwMode;

    /// <summary>Status request (GetFeature, nothing is sent to the keypad) → mode. Logged when it changes.</summary>
    internal async Task<HwMode> RefreshHwMode(string why)
    {
        if (_offline || !_connected || _busy || _modeBusy) return _hwMode;
        _modeBusy = true;
        try
        {
            var st = await Task.Run(() => { using var d = K13Device.OpenFirst(500); return d?.GetStatus(); });
            if (st != null) SetHwMode(st, why);
        }
        catch (Exception ex) { App.Log("[mode] статус: " + ex.Message); }
        finally { _modeBusy = false; }
        return _hwMode;
    }

    private void SetHwMode(K13Status st, string why)
    {
        _status = st;
        var m = K13Mode.FromStatus(st);
        if (m != _hwMode)
        {
            Log($"[mode] режим клавиатуры: {K13Mode.Name(_hwMode)} → {K13Mode.Name(m)} ({why}; статус байт 3 = {(st.Raw.Length > 3 ? st.Raw[3].ToString("X2") : "—")})");
            var was = _hwMode;
            _hwMode = m;
            OnHwModeChanged(was, m);   // 1.3.0: lighting for Normal / Game
            RefreshModeLightPanel();
            if (m != HwMode.Game) _selfGame = false;
            if (m == HwMode.Game) ClearKeypadWarnings("режим Game");
            UpdateConn(); RefreshModeBanner();
        }
    }

    /// <summary>Every 3rd poll (≈6 s) while the window is visible — so a stick press (Normal ↔ Game) shows up.
    /// 1.3.0: with «Подсветка в режиме Normal» set — every poll (2 s), also in the tray (status read only, nothing is written).</summary>
    private async Task PollHwMode()
    {
        if (_bisecting) return;
        if (ModeLightingOn) { await RefreshHwMode("опрос"); return; }
        if (++_modeTick % 3 != 0 || !IsVisible || WindowState == WindowState.Minimized) return;
        await RefreshHwMode("опрос");
    }

    private string ModeSuffix() => !_connected ? "" : _hwMode switch
    {
        HwMode.Game => _selfGame ? " · режим Game (ваши назначения, запомнен клавиатурой)" : " · режим Game (ваши назначения)",
        HwMode.Normal => " · режим Normal (заводские клавиши)",
        _ => "",
    };

    private const string ModeExplain =
        "У K13 два аппаратных режима. Normal — заводские клавиши (Num 1…, стрелки). Game — ваши назначения, которые записывает программа; горит синий индикатор между Tab и 9. " +
        "После подключения клавиатура всегда начинает в Normal — это не сброс настроек. Переключает режимы заводское нажатие стика (центр). Команды «включить Game» у клавиатуры нет — программа режим не переключает. Чтобы Game сохранялся после переподключения, включите ⋯ → «Запоминать режим».\n" +
        "Профили GAME и Normal в программе производителя записывают одну и ту же раскладку — режима Game; заводской режим Normal не меняется.";
}
