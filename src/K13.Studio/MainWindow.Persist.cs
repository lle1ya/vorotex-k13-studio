using System.Windows;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.7.3: «do the settings survive a replug?» — friendly hint after a write, a read-only check after reconnecting,
/// and a note after «Прочитать» (VOROTEX report 16.11.2026: written layout lost after USB replug).</summary>
public partial class MainWindow
{
    private bool _replugAsked;

    private void ShowWrittenHint(double secs)
    {
        WrittenHintDialog(secs, out var answer).ShowDialog();
        if (answer() == MessageBoxResult.No) SaveDiagnostics();
    }

    internal Window WrittenHintDialog(double secs, out Func<MessageBoxResult> answer)
    {
        var text = $"Готово за {secs:0.0} с: настройки записаны и проверены чтением.\n\n" +
                   "Ваши назначения работают в режиме Game (горит синий индикатор между Tab и 9). После переподключения клавиатура начинает в режиме Normal — " +
                   "с заводскими клавишами; это не сброс. Game включает нажатие стика, а при запущенной программе — она сама.\n\n" +
                   "Если в режиме Game кнопки делают не то — сохраните диагностику и пришлите нам.";
        return MessageBox.Build(IsLoaded ? this : null, text, Title, MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes, out answer,
            new[] { "Понятно", "Сохранить диагностику" });
    }

    /// <summary>Smoke test: pretend the keypad sent a key its memory cannot produce (0 = reset).</summary>
    internal void SimulateForeignKey(ushort vk) { _kpForeignVk = vk; RefreshModeBanner(); }

    /// <summary>Called on (re)connect. Never reads by itself — asks first (v4.5 rule: no automatic read on connect).</summary>
    private async void AskReplugCheck()
    {
        var pending = _store.Data.ReplugCheck;
        if (pending == null || _replugAsked || _bisecting || _busy || _offline || !_connected || !IsLoaded) return;
        _replugAsked = true;
        Log($"Ожидается проверка после переподключения (запись {pending.WrittenAt:dd.MM HH:mm}, {pending.Blocks.Count} блок(ов)).");
        MessageBox.Build(this, "Клавиатура снова подключена.\n\nПроверить, сохранились ли записанные настройки? Программа только прочитает память (≈ 10 с) и сверит её с записанным.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes, out var answer, new[] { "Проверить", "Позже" }).ShowDialog();
        if (answer() != MessageBoxResult.Yes) { Log("Проверка после переподключения отложена."); return; }
        await RunReplugCheck(pending);
    }

    private async Task RunReplugCheck(PendingReplugCheck pending)
    {
        var img = await ReadDevice(auto: false, prefix: "k13-afterreplug");
        if (img == null) return;
        var bad = pending.Differences(img);
        _store.Data.ReplugCheck = null; SaveStore();
        string head = HeaderHex(img);
        if (bad.Count == 0)
        {
            ClearKeypadWarnings("память после переподключения совпала");
            Log($"[replug] После переподключения память совпадает с записанным ({pending.Blocks.Count} блок(ов)). Заголовок {head}. Сравнивали с {pending.PostWriteFile}.");
            Ask("Память после переподключения совпадает с записанным — назначения сохранились.\n\nЕсли кнопки делают заводское (например, Num 1), клавиатура в режиме Normal: нажмите стик " +
                "(загорится синий индикатор между Tab и 9) или ⋯ → «Включить Game сейчас».");
        }
        else
        {
            Log($"[replug] После переподключения память ОТЛИЧАЕТСЯ от записанного в {bad.Count} байт(ах): " + string.Join(", ", bad.Take(16).Select(a => $"0x{a:X3}")) + $". Заголовок {head}.");
            Ask($"После переподключения память отличается от записанного ({bad.Count} байт) — настройки не сохранились.\n\nСохраните диагностику и пришлите нам.");
        }

        void Ask(string text)
        {
            MessageBox.Build(this, text, "Проверка после переподключения", MessageBoxButton.YesNo, bad.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning,
                MessageBoxResult.No, out var a, new[] { "Сохранить диагностику", "Закрыть" }).ShowDialog();
            if (a() == MessageBoxResult.Yes) SaveDiagnostics();
        }
    }

    /// <summary>After «Прочитать → заменить»: the UI now shows the keypad memory. If the header is not the vendor one, say what that means.</summary>
    private void AfterReadNote(byte[] img)
    {
        _ = Dispatcher.BeginInvoke(async () => { await Task.Delay(400); await RefreshHwMode("после чтения"); }); // v5.8: the read's session end brings Game
        if (TryDecode(img) is { } mem)
        {
            var empty = StickTilts.UnassignedControls(mem);
            if (empty.Count > 0)
                Log("[read] В памяти не назначены (FF, ничего не посылают): " + string.Join(", ", empty.Select(c => DeviceView.ControlName(c.Column))) +
                    (StickTilts.Unassigned(mem).Count > 0 ? ". Наклонам стика в профиле назначены стрелки — нажмите «Записать»." : "."));
            if (StickTilts.Unassigned(mem).Count > 0)
            {
                StatusText.Text = "Наклоны стика в клавиатуре не назначены — нажмите «Записать», чтобы прописать стрелки.";
                return;
            }
        }
        var h = K13Header.Inspect(img);
        if (h.Verdict == HeaderVerdict.Exact) return;
        Log($"[read] Заголовок памяти {h.RawHex}: не E1 1E. Программа фирмы пишет туда E1 1E; v5.7.3 тоже (при следующей записи).");
        StatusText.Text = "Показано то, что записано в памяти клавиатуры. Если кнопки печатают другое — запишите профиль ещё раз и переподключите клавиатуру.";
    }

    /// <summary>Short list «кнопка = назначение» for the log (what the UI now shows after a read).</summary>
    private static string DescribeKeys(K13DeviceConfig c) =>
        string.Join(", ", Enumerable.Range(0, K13Layout.KeymapColumns).Where(col => c.GetKey(col) != KeyCatalog.Disabled)
            .Select(col => $"{col}={KeyCatalog.Get(c.GetKey(col)).Name}"));
}
