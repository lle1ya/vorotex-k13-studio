using System.IO;
using System.Windows;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.8 (corrects v5.7.4): the K13 boots in hardware mode Normal (factory table); the written keymap is the Game layer.
/// The stick (0xF4) toggles the modes; a host read exchange also brings Game (16.11.2026 15:07). «Включить Game сейчас» sends that
/// exchange — read-type frames only — and checks the mode via the status block (PROTOCOL §30).</summary>
public partial class MainWindow
{
    private bool _bisecting;
    private List<ushort>? _bisectKeys;
    private DateTime _activatedAt = DateTime.MinValue;
    /// <summary>A key not from memory arrived after an activation — the short exchange was not enough.</summary>
    private bool _activationFailed;

    private async void OnConnectedAsync()
    {
        if (_bisecting) { Log("[bisect] клавиатура подключена — ничего не отправляю (идёт тест)."); return; }
        if (_store.Data.ActivateOnConnect && !_offline)
        {
            if (_hwMode == HwMode.Game) Log("[activate] клавиатура уже в режиме Game — ничего не отправляю.");
            else await ActivateKeypad(auto: true);
        }
        AskReplugCheck();
    }

    /// <summary>Short read of the header block + session end (07 18 00 / 05 / 01, 07 18 11, 07 18 00, 07 20). Never writes.</summary>
    internal async Task<bool> ActivateKeypad(bool auto)
    {
        if (_busy || _offline || !_connected) return false;
        _busy = true; UpdateConn();
        try
        {
            var (head, frames) = await Task.Run(() =>
            {
                using var dev = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена.");
                var h = K13Activation.Activate(dev);
                return (h, dev.SentTimes.ToList());
            });
            if (frames.Any(f => !K13Protocol.IsReadOnlyFrame(f.Frame) && !K13Protocol.IsSessionEndFrame(f.Frame)))
                throw new InvalidOperationException("в активации оказалась нечитающая команда");
            _activatedAt = DateTime.Now; _kpForeignVk = 0; _activationFailed = false;
            var before = _hwMode;
            Log($"[activate] {(auto ? "при подключении" : "вручную")}: {frames.Count} кадров чтения/завершения, без записи; заголовок {Convert.ToHexString(head)}. " +
                string.Join(" · ", frames.Select(f => $"{f.Ms}мс {Convert.ToHexString(f.Frame)}")));
            _busy = false;
            var after = await RefreshHwMode("после включения Game");
            Log($"[activate] режим до: {K13Mode.Name(before)}, после: {K13Mode.Name(after)}.");
            StatusText.Text = after == HwMode.Game ? "Включён режим Game — работают ваши назначения (только чтение, без записи)."
                : after == HwMode.Normal ? "Клавиатура осталась в режиме Normal. Нажмите стик или проверьте, что назначения записаны."
                : "Отправлен обмен чтения, режим определить не удалось. Проверьте кнопки.";
            RefreshModeBanner();
            if (auto && !_store.Data.StartWithWindows && !_store.Data.ActivateAutostartHintSeen && IsLoaded)
                await Dispatcher.InvokeAsync(SuggestAutostart, DispatcherPriority.Background);
            return true;
        }
        catch (Exception ex)
        {
            Log("[activate] не удалось: " + ex.Message);
            return false;
        }
        finally { _busy = false; UpdateConn(); ExitIfPending(); }
    }

    private void SuggestAutostart()
    {
        _store.Data.ActivateAutostartHintSeen = true; SaveStore();
        MessageBox.Build(this,
            "После подключения K13 начинает в режиме Normal (заводские клавиши), а ваши назначения — это режим Game. Его включает нажатие стика или программа: с настройкой ⋯ → «Автоматически включать Game при подключении» она делает это сама (только чтение, без записи).\n\n" +
            "Чтобы это работало и после перезагрузки компьютера, включите запуск вместе с Windows: программа будет тихо работать в трее.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes, out var a, new[] { "Включить автозапуск", "Не сейчас" }).ShowDialog();
        if (a() == MessageBoxResult.Yes) AutostartBox.IsChecked = true;
    }

    internal void SetActivateOnConnect(bool on)
    {
        _store.Data.ActivateOnConnect = on; SaveStore();
        Log(on ? "Настройка «Автоматически включать Game при подключении» включена." : "Настройка «Автоматически включать Game при подключении» выключена.");
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
        bool go = AskBisect(text + "\n\nПосмотрите на синий индикатор между Tab и 9 (горит = режим Game). Нажмите проверочную кнопку 2–3 раза, затем «Дальше».");
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
