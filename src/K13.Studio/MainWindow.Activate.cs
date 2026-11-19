using System.IO;
using System.Windows;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.8 (corrects v5.7.4): the K13 boots in hardware mode Normal (factory table); the written keymap is the Game layer.
/// The stick (0xF4) toggles the modes; a host read exchange sometimes brings Game right after connect (16.11.2026 15:07).
/// 1.0.1: only the connect-time attempt is left (read-type frames only, mode checked via status, PROTOCOL §30, §35); the manual
/// «Включить Game сейчас» was removed — on the makers' K13 it never switched (17.11.2026).</summary>
public partial class MainWindow
{
    private bool _bisecting;
    private List<ushort>? _bisectKeys;

    /// <summary>1.0.5: the connect-time «Пробовать включать Game» exchange is gone. The makers' diagnostics (18.11.2026 09:10–09:18)
    /// showed it never moved the keypad Normal → Game: the three «successes» were all replugs of a keypad that was already in Game
    /// (the first status at connect always reads Normal, then the keypad reports its restored mode), and all six replugs from Normal
    /// stayed Normal. Nothing is sent to the keypad on connect now; the stick and «Запоминать режим» are the way (PROTOCOL §39).</summary>
    private void OnConnectedAsync()
    {
        if (_bisecting) { Log("[bisect] клавиатура подключена — ничего не отправляю (идёт тест)."); return; }
        AskReplugCheck();
    }

    // ───────────── bisect: which single frame switches the layout on

    private Window BisectDialog(string text, string ok, string cancel, out Func<MessageBoxResult> answer) =>
        MessageBox.Build(IsLoaded ? this : null, text, "Поиск команды, включающей раскладку", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes, out answer, new[] { ok, cancel });

    private bool AskBisect(string text, string ok = "Дальше", string cancel = "Прервать")
    {
        BisectDialog(text, ok, cancel, out var a).ShowDialog();
        return a() == MessageBoxResult.Yes;
    }

    private K13Activation.Seen PressAndClassify(string text, out string keys)
    {
        _bisectKeys = new List<ushort>();
        bool go = AskBisect(text + (K13ModeHints.IndicatorOff(RememberState.Value) ? "\n\nСиний индикатор у вас выключен — смотрите на строку режима под названием в Studio. Нажмите" : "\n\nПосмотрите на синий индикатор между Tab и 9 (горит = режим Game). Нажмите") + " проверочную кнопку 2–3 раза, затем «Дальше».");
        var got = _bisectKeys; _bisectKeys = null;
        keys = got.Count == 0 ? "ничего" : string.Join(", ", got.Select(VkName));
        if (!go) throw new OperationCanceledException();
        return K13Activation.Classify(KeypadCfg, got);
    }

    private static string SeenText(K13Activation.Seen s) => s switch
    {
        K13Activation.Seen.Saved => "ЗАПИСАННАЯ раскладка",
        K13Activation.Seen.Factory => "заводская раскладка",
        K13Activation.Seen.Mixed => "смешанно",
        _ => "клавиатура ничего не прислала",
    };

    internal async void RunBisect()
    {
        if (_busy || _offline || _bisecting) return;
        if (KeypadCfg == null) { MessageBox.Show(this, "Сначала прочитайте клавиатуру или запишите профиль: программе нужно знать, что лежит в её памяти.", Title); return; }
        if (!AskBisect("Тест найдёт, какая именно команда переключает клавиатуру из Normal (заводские клавиши) в Game (ваши назначения).\n\n" +
                       "Программа будет по одной отправлять команды, которые она и так посылает при чтении памяти (ничего не записывается). " +
                       "После каждой нужно нажать проверочную кнопку — например, кнопку 4, если на неё записана F5, а заводская — Num 4. " +
                       "Программа сама увидит, что прислала клавиатура.\n\nЗаймёт 2–3 минуты.", "Начать", "Отмена")) return;
        _bisecting = true;
        var results = new List<string>();
        string? found = null;
        Log("[bisect] начат.");
        try
        {
            if (!AskBisect("Выньте USB-кабель клавиатуры, подождите 2–3 секунды и вставьте снова. Не нажимайте стик — он сам переключает режим.\nПрограмма в это время ничего не отправит.\n\nКогда клавиатура подключится — «Дальше».")) throw new OperationCanceledException();
            for (int i = 0; i < 10 && !_connected; i++) await Task.Delay(500);
            if (!_connected) { MessageBox.Show(this, "Клавиатура не подключена — тест прерван.", Title); return; }
            var m0 = await BisectMode();
            var s0 = PressAndClassify($"Шаг 0: ничего не отправлено. Режим по статусу: {K13Mode.Name(m0)}.", out var k0);
            results.Add($"0. ничего не отправлено → режим {K13Mode.Name(m0)} → {k0} → {SeenText(s0)}");
            Log("[bisect] " + results[^1]);
            if (s0 == K13Activation.Seen.Saved || m0 == HwMode.Game)
            {
                found = "ничего: клавиатура уже в режиме Game (возможно, нажимали стик) — переподключите её и не нажимайте стик до начала теста";
            }
            else
            {
                var steps = K13Activation.BisectSteps;
                for (int n = 0; n < steps.Count && found == null; n++)
                {
                    var st = steps[n];
                    var sent = await Task.Run(() =>
                    {
                        using var dev = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена.");
                        dev.EndSessionOnDispose = false; // only this step's frames
                        st.Run(dev);
                        var list = dev.SentTimes.Select(f => Convert.ToHexString(f.Frame)).ToList();
                        return (list, dev.GetStatus());
                    }) is var (sentList, stAfter) ? sentList : new();
                    var mode = K13Mode.FromStatus(stAfter);
                    _status = stAfter;
                    Log($"[bisect] шаг {n + 1} «{st.Name}» отправлено: " + (sent.Count == 0 ? "только GetFeature" : string.Join(" · ", sent)));
                    var s = PressAndClassify($"Шаг {n + 1} из {steps.Count}: отправлено «{st.Name}» ({st.Frames}).\nРежим по статусу: {K13Mode.Name(mode)} (байт 3 = {stAfter.Raw[3]:X2}).", out var keys);
                    results.Add($"{n + 1}. {st.Name} ({st.Frames}) → режим {K13Mode.Name(mode)} → {keys} → {SeenText(s)}");
                    Log("[bisect] " + results[^1]);
                    if (s == K13Activation.Seen.Saved || mode == HwMode.Game) found = $"шаг {n + 1}: {st.Name} ({st.Frames}) — режим Game";
                }
            }
        }
        catch (OperationCanceledException) { results.Add("прервано пользователем"); Log("[bisect] прерван."); }
        catch (Exception ex) { results.Add("ошибка: " + ex.Message); Log("[bisect] ошибка: " + ex.Message); }
        finally
        {
            _bisecting = false; _bisectKeys = null;
            // leave the keypad in its normal state whatever happened (same frames as after every read)
            try { await Task.Run(() => { using var d = K13Device.OpenFirst(); d?.EndSession(); }); Log("[bisect] сеанс завершён (07 18 11 / 07 18 00 / 07 20)."); }
            catch (Exception ex) { Log("[bisect] завершение сеанса: " + ex.Message); }
        }
        Log("[bisect] итог: " + (found ?? "не найдено"));
        MessageBox.Build(this, (found != null ? "Режим Game включил " + found + ".\n\n" : "Ни одна отдельная команда не включила режим Game.\n\n") +
                               string.Join("\n", results) + "\n\nСохраните диагностику и пришлите нам — результат уже в журнале.",
            "Поиск команды, включающей Game — результат", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes, out var a, new[] { "Сохранить диагностику", "Закрыть" }).ShowDialog();
        if (a() == MessageBoxResult.Yes) SaveDiagnostics();
    }

    private async Task<HwMode> BisectMode()
    {
        try
        {
            var st = await Task.Run(() => { using var d = K13Device.OpenFirst(2000); return d?.GetStatus(); });
            if (st != null) { _status = st; return K13Mode.FromStatus(st); }
        }
        catch (Exception ex) { Log("[bisect] статус: " + ex.Message); }
        return HwMode.Unknown;
    }
}
