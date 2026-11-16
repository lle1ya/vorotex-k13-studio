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
            _hwMode = m;
            if (m == HwMode.Game) { _kpForeignVk = 0; _activationFailed = false; }
            UpdateConn(); RefreshModeBanner();
        }
    }

    /// <summary>Every 3rd poll (≈6 s) while the window is visible — so a stick press (Normal ↔ Game) shows up.</summary>
    private async Task PollHwMode()
    {
        if (++_modeTick % 3 != 0 || !IsVisible || WindowState == WindowState.Minimized || _bisecting) return;
        await RefreshHwMode("опрос");
    }

    private string ModeSuffix() => !_connected ? "" : _hwMode switch
    {
        HwMode.Game => " · режим Game (ваши назначения)",
        HwMode.Normal => " · режим Normal (заводские клавиши)",
        _ => "",
    };

    private const string ModeExplain =
        "У K13 два аппаратных режима. Normal — заводские клавиши (Num 1…, стрелки). Game — ваши назначения, которые записывает программа; горит синий индикатор между Tab и 9. " +
        "После подключения клавиатура всегда начинает в Normal — это не сброс настроек. Переключает режимы заводское нажатие стика; программа включает Game сама при подключении (настройка ⋯ → «Автоматически включать Game при подключении») или по команде ⋯ → «Включить Game сейчас» — только чтение, без записи.\n" +
        "Профили GAME и Normal в программе производителя записывают одну и ту же раскладку — режима Game; заводской режим Normal не меняется.";
}
