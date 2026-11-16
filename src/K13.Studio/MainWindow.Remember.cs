using System.IO;
using System.Windows;
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
        string s = v is not byte b ? "неизвестно" : (K13RememberMode.IsOn(b) ? "вкл" : "выкл") + (fresh ? "" : ", по последнему чтению");
        return $"Запоминать режим (эксперимент)… — {s}";
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

    internal Window RememberDialog(byte? cur, bool canWrite, out Func<MessageBoxResult> answer)
    {
        string state = cur is byte b ? K13RememberMode.Describe(b) : "неизвестно (клавиатура не прочитана)";
        string text = $"Сейчас: {state}.\n\n" + RememberExplain +
                      (canWrite ? "" : "\n\nПодключите клавиатуру, чтобы изменить настройку.");
        if (!canWrite || cur is not byte c)
            return MessageBox.Build(IsLoaded ? this : null, text, "Запоминать режим (эксперимент)", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, out answer);
        if (!K13RememberMode.IsKnownPattern(c))
            return MessageBox.Build(IsLoaded ? this : null, text + $"\n\n⚠ Значение 0x{c:X2} незнакомое — программа его менять не будет.", "Запоминать режим (эксперимент)",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, out answer, new[] { "Восстановить из копии…", "Закрыть" });
        return MessageBox.Build(IsLoaded ? this : null, text, "Запоминать режим (эксперимент)", MessageBoxButton.YesNoCancel, MessageBoxImage.Information, MessageBoxResult.Cancel, out answer,
            new[] { K13RememberMode.IsOn(c) ? "Выключить" : "Включить (эксперимент)", "Восстановить из копии…", "Закрыть" });
    }

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
        var cur = RememberState.Value;
        RememberDialog(canWrite ? cur : RememberState.Value, canWrite, out var answer).ShowDialog();
        var a = answer();
        bool unknownPattern = cur is byte c0 && !K13RememberMode.IsKnownPattern(c0);
        if (!canWrite || cur is not byte c) return;
        if (!unknownPattern && a == MessageBoxResult.Yes)
        {
            bool enable = !K13RememberMode.IsOn(c);
            await RunRemember(x => K13RememberMode.Target(x, enable), enable ? "включить «запоминать режим»" : "выключить «запоминать режим»", c);
        }
        else if (unknownPattern ? a == MessageBoxResult.Yes : a == MessageBoxResult.No)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Резервная копия для восстановления 0x000F", Filter = "Резервная копия K13|*.bin|Все файлы|*.*", InitialDirectory = BackupStore.EnsureFolder() };
            if (dlg.ShowDialog(this) != true) return;
            byte[] bak;
            try { bak = File.ReadAllBytes(dlg.FileName); } catch (Exception ex) { MessageBox.Show(this, "Не удалось прочитать файл: " + ex.Message, Title); return; }
            var (t, err) = K13RememberMode.RestoreTarget(c, bak);
            if (t is null) { MessageBox.Show(this, err!, Title, MessageBoxButton.OK, MessageBoxImage.Warning); Log("[remember] восстановление отклонено: " + err); return; }
            Log($"[remember] восстановление из {dlg.FileName}: 0x000F = {t:X2}.");
            await RunRemember(x => K13RememberMode.RestoreTarget(x, bak), $"восстановить 0x000F = {t:X2} из «{Path.GetFileName(dlg.FileName)}»", c);
        }
    }

    private async Task RunRemember(Func<byte, (byte? Target, string? Error)> decide, string what, byte cur)
    {
        var (t, err) = decide(cur);
        if (t is not byte target) { MessageBox.Show(this, err ?? "Отменено.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (target == cur) { MessageBox.Show(this, $"Менять нечего: 0x000F уже 0x{cur:X2}.", Title); return; }
        if (MessageBox.ShowCustom(this,
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
            MessageBox.Show(this, r.Message + $"\n\nРезервная копия до изменения:\n{r.BackupPath}" +
                (r.Ok && r.Written && K13RememberMode.IsOn(r.After) ? "\n\nТеперь проверьте: снимите галочку «Автоматически включать Game при подключении», включите Game стиком, закройте программу и переподключите USB — горит ли синий индикатор?" : ""),
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
