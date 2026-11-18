using System.Windows;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>1.3.1: «На клавиатуре — заводские назначения». After «Вернуть заводские назначения» (or when the keypad memory shows the
/// factory assignments) the PC profiles stay on the PC: nothing is written to the keypad automatically (profile switch lighting,
/// auto-switch, Normal/Game lighting) and auto-switching is suspended, until the user presses «Применить профиль». Makers' request
/// 18.11.2026: preparing a K13 for sale, it must be obvious which layout ships.</summary>
public partial class MainWindow
{
    private bool _factoryAutoLogged, _factoryQuietLogged;

    /// <summary>The keypad holds the factory assignments: from its memory image when one is known, else the stored flag.</summary>
    internal bool FactoryOnKeypad => KeypadCfg is { } kp ? K13Model.Detect(_deviceImage) == KeypadModel.K13 && K13FactoryAssignments.IsFactory(kp) : _store.Data.KeypadFactory;

    /// <summary>Factory state that differs from the active profile — the profile is NOT on the keypad and is written only by «Применить профиль».
    /// (A profile that itself has the factory assignments, e.g. «Заводские назначения» from 1.2.0, is simply «in sync».)</summary>
    internal bool FactoryLock => FactoryOnKeypad && !K13FactoryAssignments.IsFactory(DeviceTarget);

    /// <summary>Keeps the stored flag in step with the image (so the state survives a restart without a cached image).</summary>
    private void SyncFactoryFlag()
    {
        if (KeypadCfg == null) return;
        bool f = FactoryOnKeypad;
        if (f == _store.Data.KeypadFactory) return;
        _store.Data.KeypadFactory = f;
        SaveStore();
        Log(f ? "[factory] на клавиатуре — заводские назначения (по её памяти)." : "[factory] на клавиатуре больше не заводские назначения.");
        if (!f) { _factoryAutoLogged = _factoryQuietLogged = false; }
    }

    internal const string FactoryStateText = "На клавиатуре — заводские назначения";

    internal string FactoryBannerText() =>
        $"{FactoryStateText}: кнопки, колесо и стик работают по-заводски, без макросов и Fn. Профиль «{_profile.Name}» хранится на ПК и в клавиатуру не записывается — " +
        "ни при смене профиля, ни автопереключением (оно приостановлено), ни подсветкой. Записать профиль — «Применить профиль».";

    /// <summary>«Применить профиль» (the Write button in the factory state): explicit confirmation, then the usual write.</summary>
    internal string ApplyOverFactoryText() =>
        $"Применить профиль «{_profile.Name}» к клавиатуре?\n\n" +
        $"Сейчас на клавиатуре заводские назначения. После записи кнопки, колесо и макросы будут как в профиле «{_profile.Name}».\n\n" +
        "Как обычно: сначала резервная копия, затем запись изменённых блоков и проверка чтением.";

    internal Window BuildApplyOverFactory(out Func<MessageBoxResult> answer) =>
        MessageBox.Build(IsLoaded ? this : null, ApplyOverFactoryText(), "Применить профиль", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No, out answer, new[] { "Применить профиль", "Отмена" });

    private bool ConfirmApplyOverFactory()
    {
        BuildApplyOverFactory(out var ans).ShowDialog();
        return ans() == MessageBoxResult.Yes;
    }

    /// <summary>Automatic (quiet) writes are skipped in the factory state.</summary>
    private bool FactoryBlocksQuiet()
    {
        if (!FactoryLock) return false;
        if (!_factoryQuietLogged) { _factoryQuietLogged = true; Log("[factory] на клавиатуре заводские назначения — автоматическая запись (подсветка) не выполняется до «Применить профиль»."); }
        return true;
    }

    /// <summary>Auto-switching is suspended in the factory state (it would only change the PC side and confuse which profile is applied).</summary>
    private bool FactoryBlocksAuto(string profile)
    {
        if (!FactoryLock) return false;
        if (!_factoryAutoLogged) { _factoryAutoLogged = true; Log($"[factory] автопереключение на «{profile}» пропущено: на клавиатуре заводские назначения (до «Применить профиль»)."); }
        return true;
    }

    private void RefreshWriteBtnText()
    {
        if (WriteBtnText == null) return;
        bool f = FactoryLock;
        WriteBtnText.Text = f ? "Применить профиль" : "Записать";
    }

    // smoke
    internal void DemoKeypadImage(byte[]? img) { _deviceImage = img; _imageFresh = img != null; RefreshDirty(); }
    internal string WriteButtonText => WriteBtnText.Text;
    internal void DemoAutoDecided(string profile) => OnAutoDecided(new AutoSwitchDecision(profile, "проверка", false, null), "smoke.exe", "проверка");
    internal K13DeviceConfig DemoTarget => DeviceTarget;
    internal void DemoFactoryFlag(bool on) { _store.Data.KeypadFactory = on; RefreshDirty(); }
    internal bool DemoLightingPending => LightingPending();
}
