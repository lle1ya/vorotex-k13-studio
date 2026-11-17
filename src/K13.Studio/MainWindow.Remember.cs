using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.9, EXPERIMENTAL: «Запоминать режим» = factory flag FN_M_MODE_SV_EN, EEPROM 0x000F bit 1 (PROTOCOL §31).
/// Every write: explicit confirmation, full backup first, one byte, read-back check. 0x0000–0x0007 are refused in K13Device.</summary>
public partial class MainWindow
{
    private byte? _rememberByte;          // last value read from the keypad in this session
    private bool _rememberFresh;

    /// <summary>0x000F as known: a read of the factory block in this session, else the last image (may be stale).</summary>
    internal (byte? Value, bool Fresh) RememberState =>
        _rememberByte is byte b && _rememberFresh ? (b, true)
        : _deviceImage is { Length: > K13RememberMode.Address } img ? (img[K13RememberMode.Address], _imageFresh) : (null, false);

    internal string RememberMenuText()
    {
        var (v, fresh) = RememberState;
        string st = v is not byte b ? "?" : (K13RememberMode.IsOn(b) ? "вкл" : "выкл") + (fresh ? "" : " (по последнему чтению)");
        return $"Запоминать режим (эксперимент): {st}…";
    }

    private int BaseAddr => _status?.Base is int b and > 0 ? b : K13Layout.DefaultBase;

    internal string RememberInfo()
    {
        var (v, fresh) = RememberState;
        string baseFlags = _deviceImage is { } img && BaseAddr < img.Length ? img[BaseAddr].ToString("X2") + $" (бит 7 KEYSMODE = {(img[BaseAddr] >> 7) & 1})" : "—";
        return $"EEPROM 0x000F: {(v is byte b ? b.ToString("X2") : "—")} — «запоминать режим» (бит 1): {K13RememberMode.Describe(v)}{(v != null && !fresh ? ", по последнему известному образу" : "")}\n" +
               $"BASE+0 (0x{BaseAddr:X4}): {baseFlags}";
    }

    internal const string RememberExplain =
        "ЭКСПЕРИМЕНТ. В заводском блоке памяти K13 есть флаг «запоминать режим» (ячейка 0x000F, бит 1). Сейчас он выключен (0x91) — поэтому после подключения клавиатура всегда начинает в режиме Normal.\n\n" +
        "Если его включить (0x93), клавиатура, вероятно, будет помнить последний режим: включили Game — после переподключения снова Game, даже без программы. " +
        "Уверенность ≈ 60–65 %: вывод сделан по программе прошивки производителя, на клавиатуре ещё не проверялся. Возможно, флаг ничего не изменит.\n\n" +
        "Что делает программа: сохраняет полную резервную копию памяти, читает ячейку, меняет в ней только бит 1 (один байт, те же команды записи, что и обычно), читает обратно и сверяет. " +
        "Байты 0x0000–0x0007 (тип памяти, VID/PID) программа не записывает никогда. Выключить можно здесь же или восстановить ячейку из резервной копии.\n\n" +
        "Как проверить:\n" +
        "1. Снимите галочку ⋯ → «Автоматически включать Game при подключении», чтобы она не подменяла результат.\n" +
        "2. Включите Game (стиком — загорится синий индикатор между Tab и 9), закройте программу полностью (и в трее).\n" +
        "3. Выньте и вставьте USB. Горит синий индикатор и кнопки делают ваши назначения — флаг работает.\n" +
        "4. Переключите стиком в Normal (индикатор погаснет) и снова переподключите. Если осталась Normal — клавиатура помнит именно последний режим.\n" +
        "5. Сообщите результат; при желании выключите флаг здесь же.";

    internal enum RememberChoice { Close, Enable, Disable, Restore }

    /// <summary>v5.9.2: the state is a big line; ON → default «Готово», disabling is a separate secondary button with its own confirm;
    /// OFF → «Включить» is primary. No button changes meaning in the default position.</summary>
    internal Window RememberDialog(byte? cur, bool canWrite, out Func<RememberChoice> answer)
    {
        var choice = RememberChoice.Close;
        answer = () => choice;
        var win = Ui.MakeWindow(IsLoaded ? this : null, "Запоминать режим (эксперимент)", 620, 200);
        win.ResizeMode = ResizeMode.NoResize; win.SizeToContent = SizeToContent.Height;
        bool known = cur is byte k && K13RememberMode.IsKnownPattern(k);
        bool on = cur is byte c1 && K13RememberMode.IsOn(c1);
        var root = new DockPanel { Margin = new Thickness(24, 14, 24, 20) };
        var status = new TextBlock
        {
            Text = cur is null ? "Сейчас: НЕИЗВЕСТНО" : !known ? $"Сейчас: НЕЗНАКОМОЕ ЗНАЧЕНИЕ 0x{cur:X2}" : on ? "Сейчас: ВКЛЮЧЕНО" : "Сейчас: ВЫКЛЮЧЕНО",
            FontSize = 22, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4),
            Foreground = (System.Windows.Media.Brush)FindResource(!known ? "WarnBrush" : on ? "OkBrush" : "MutedBrush"),
        };
        DockPanel.SetDock(status, Dock.Top); root.Children.Add(status);
        var sub = new TextBlock
        {
            Text = cur is null ? "Клавиатура не прочитана." + (canWrite ? "" : " Подключите клавиатуру, чтобы изменить настройку.")
                 : !known ? "Программа такое значение менять не будет."
                 : on ? $"Клавиатура должна помнить режим после переподключения (0x000F = {cur:X2}). Ничего нажимать не нужно — закройте программу и переподключите USB."
                      : $"Клавиатура после подключения начинает в Normal (0x000F = {cur:X2}).",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 10),
        };
        DockPanel.SetDock(sub, Dock.Top); root.Children.Add(sub);
        var bar = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        DockPanel.SetDock(bar, Dock.Bottom); root.Children.Add(bar);
        root.Children.Add(new ScrollViewer
        {
            MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = RememberExplain, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19, Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush") },
        });
        Button B(string text, RememberChoice ch, bool primary = false, bool cancel = false, bool left = false)
        {
            var b = new Button { Content = text, MinWidth = 96, Padding = new Thickness(16, 7, 16, 7), IsDefault = primary, IsCancel = cancel,
                                 Margin = left ? new Thickness(0, 0, 8, 0) : new Thickness(8, 0, 0, 0) };
            b.Style = (Style)FindResource(primary ? "AccentButton" : left ? "GhostButton" : "GhostButton");
            b.Click += (_, _) => { choice = ch; win.Close(); };
            DockPanel.SetDock(b, left ? Dock.Left : Dock.Right);
            if (primary) win.Loaded += (_, _) => b.Focus();
            return b;
        }
        // right group (added right-to-left): the default action is always the rightmost
        if (canWrite && known && !on)
        {
            bar.Children.Add(B("Включить (эксперимент)", RememberChoice.Enable, primary: true));
            bar.Children.Add(B("Закрыть", RememberChoice.Close, cancel: true));
        }
        else bar.Children.Add(B("Готово", RememberChoice.Close, primary: true, cancel: true));
        // left group: secondary actions with their own confirmation
        if (canWrite && cur is byte && on && known) bar.Children.Add(B("Выключить запоминание…", RememberChoice.Disable, left: true));
        if (canWrite && cur is byte) bar.Children.Add(B("Восстановить из копии…", RememberChoice.Restore, left: true));
        win.Content = root;
        return win;
    }

    internal static Window DisableConfirm(Window? owner, byte cur, out Func<MessageBoxResult> answer) =>
        MessageBox.Build(owner,
            "Выключить запоминание?\n\nКлавиатура снова будет стартовать в Normal (заводские клавиши), режим Game придётся включать стиком или программой.\n\n" +
            $"• Ячейка 0x000F: 0x{cur:X2} → 0x{cur & ~K13RememberMode.Bit:X2} (только бит 1). Перед записью — полная резервная копия, после — проверка.",
            "Выключить запоминание режима", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, out answer, new[] { "Выключить", "Не выключать" });

    internal async void ShowRememberDialog()
    {
        bool canWrite = _connected && !_busy && !_offline;
        if (canWrite)
        {   // read-only: factory block 0x0008–0x000F
            try
            {
                var blk = await Task.Run(() => { using var d = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена."); return d.ReadBlocks(new[] { (K13RememberMode.BlockAddress, 8) })[0]; });
                _rememberByte = blk[7]; _rememberFresh = true;
                Log($"[remember] прочитано 0x0008–0x000F = {Convert.ToHexString(blk)}: {K13RememberMode.Describe(_rememberByte)}.");
            }
            catch (Exception ex) { Log("[remember] чтение 0x000F не удалось: " + ex.Message); canWrite = false; }
        }
        var cur = canWrite ? _rememberByte : RememberState.Value;
        RememberDialog(cur, canWrite, out var answer).ShowDialog();
        var a = answer();
        Log($"[remember] окно закрыто: {a}.");
        if (!canWrite || cur is not byte c) return;
        switch (a)
        {
            case RememberChoice.Enable:
                await RunRemember(x => K13RememberMode.Target(x, true), "включить «запоминать режим»", c, confirmed: false);
                break;
            case RememberChoice.Disable:
                DisableConfirm(this, c, out var yes).ShowDialog();
                if (yes() != MessageBoxResult.Yes) { Log("[remember] выключение отменено."); return; }
                await RunRemember(x => K13RememberMode.Target(x, false), "выключить «запоминать режим»", c, confirmed: true);
                break;
            case RememberChoice.Restore:
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Резервная копия для восстановления 0x000F", Filter = "Резервная копия K13|*.bin|Все файлы|*.*", InitialDirectory = BackupStore.EnsureFolder() };
                if (dlg.ShowDialog(this) != true) return;
                byte[] bak;
                try { bak = File.ReadAllBytes(dlg.FileName); } catch (Exception ex) { MessageBox.Show(this, "Не удалось прочитать файл: " + ex.Message, Title); return; }
                var (t, err) = K13RememberMode.RestoreTarget(c, bak);
                if (t is null) { MessageBox.Show(this, err!, Title, MessageBoxButton.OK, MessageBoxImage.Warning); Log("[remember] восстановление отклонено: " + err); return; }
                Log($"[remember] восстановление из {dlg.FileName}: 0x000F = {t:X2}.");
                await RunRemember(x => K13RememberMode.RestoreTarget(x, bak), $"восстановить 0x000F = {t:X2} из «{Path.GetFileName(dlg.FileName)}»", c, confirmed: false);
                break;
        }
    }

    internal const string RememberEnabledNext = "Теперь закройте программу (и в трее) и переподключите USB. Это окно больше открывать не нужно.";

    private async Task RunRemember(Func<byte, (byte? Target, string? Error)> decide, string what, byte cur, bool confirmed)
    {
        var (t, err) = decide(cur);
        if (t is not byte target) { MessageBox.Show(this, err ?? "Отменено.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (target == cur) { MessageBox.Show(this, $"Менять нечего: 0x000F уже 0x{cur:X2}.", Title); return; }
        if (!confirmed && MessageBox.ShowCustom(this,
                $"Записать в клавиатуру: {what}?\n\n" +
                $"• Ячейка 0x000F: 0x{cur:X2} → 0x{target:X2} (меняется только бит 1, один байт).\n" +
                "• Сначала программа прочитает и сохранит полную резервную копию памяти, после записи — прочитает ячейку и сверит.\n" +
                "• Это эксперимент: эффект на клавиатуре ещё не проверялся. Клавиатуру во время записи не отключайте.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, "Записать", "Отмена") != MessageBoxResult.Yes)
        { Log("[remember] запись отменена пользователем."); return; }
        _busy = true; UpdateConn();
        StatusText.Text = "Резервная копия и запись ячейки 0x000F…";
        try
        {
            var r = await Task.Run(() =>
            {
                using var dev = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена.");
                K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
                try { return K13RememberMode.Run(dev, decide, m => App.Log(m)); }
                finally { K13WriteGate.Lock(); }
            });
            Log($"[remember] {r.Message} (было 0x{r.Before:X2}, стало 0x{r.After:X2}; BASE+0 = {(r.BaseFlags is byte bf ? bf.ToString("X2") : "—")}; резервная копия: {r.BackupPath})");
            _rememberByte = r.After; _rememberFresh = true;
            if (r.Written && _deviceImage is { Length: > K13RememberMode.Address } img) img[K13RememberMode.Address] = r.After;
            StatusText.Text = r.Message;
            bool enabledNow = r.Ok && r.Written && K13RememberMode.IsOn(r.After);
            MessageBox.Show(this, (enabledNow ? "Запоминание режима ВКЛЮЧЕНО и проверено.\n\n" + RememberEnabledNext +
                    "\n\n(Для чистой проверки галочка «Автоматически включать Game при подключении» должна быть снята. После переподключения: горит синий индикатор между Tab и 9 — работает.)"
                    : r.Message) + $"\n\nРезервная копия до изменения:\n{r.BackupPath}",
                Title, MessageBoxButton.OK, r.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log("[remember] ошибка: " + ex.Message);
            MessageBox.Show(this, "Не удалось: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; UpdateConn(); }
    }
}
