using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>
/// v5.3: headless UI smoke test — <c>VorotexK13Studio.exe --smoke-test &lt;folder&gt;</c>.
/// Offline window (no keyboard hook, no tray, no HID access, profiles in a temp folder): visits every page, every control,
/// every editor tile, every macro slot, the welcome slides and the non-modal dialogs in light and dark theme, rendering each.
/// Writes smoke.txt (one line per step) and returns the number of failed steps.
/// </summary>
public static class SmokeTest
{
    public static int Unhandled;

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var lines = new List<string>();
        int fails = 0, steps = 0;
        void Step(string name, Action a)
        {
            steps++;
            int before = Unhandled;
            try { a(); Pump(); if (Unhandled != before) throw new InvalidOperationException("необработанное исключение в диспетчере (см. studio.log)"); lines.Add("OK   " + name); }
            catch (Exception ex) { fails++; lines.Add("FAIL " + name + " — " + ex.GetType().Name + ": " + ex.Message); App.Log("[smoke] " + name + ": " + ex); }
        }
        var tmp = Path.Combine(Path.GetTempPath(), "vorotex-smoke-" + Environment.ProcessId);
        foreach (var dark in new[] { false, true })
        {
            string th = dark ? "dark" : "light";
            MainWindow? w = null;
            Step($"{th}: открыть окно", () =>
            {
                App.SetTheme(dark);
                w = new MainWindow(offline: true, storeFolder: tmp + th, previewImage: null)
                { Width = 1280, Height = 800, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                w.Show();
            });
            if (w == null) continue;
            int demo = 0;
            Step($"{th}: демо-данные", () => { demo = w.DemoMouseMacro(); w.DemoV47(demo); });
            for (int p = 0; p < 4; p++) { int pg = p; Step($"{th}: вкладка {pg}", () => Render(w, pg)); }
            var ids = K13Layout.Controls.Select(c => c.Column).Concat(new[] { DeviceView.StickPress, DeviceView.RollUp, DeviceView.RollDown, -1 }).Distinct().ToList();
            foreach (var id in ids)
            {
                Step($"{th}: кнопка {id}", () => { w.SelectControl(id); Render(w, 0); });
                if (dark) continue;
                foreach (var tile in new[] { "key", "combo", "media", "app", "macro", "mouse", "light", "ring", "diag", "off" })
                    Step($"{th}: кнопка {id} → плитка {tile}", () => { w.SelectControl(id); w.OpenTile(tile); w.ScrollEditor(400); Render(w, 0); w.OpenTile(null); });
            }
            for (int m = 0; m < K13Layout.MacroSlots; m++) { int mm = m; Step($"{th}: макрос {mm + 1}", () => { w.SelectMacro(mm); Render(w, 2); }); }
            Step($"{th}: макрос: окно проверки", () => Dialog(w.BuildTestWindow(demo)));
            Step($"{th}: повтор N раз / по кругу / пока зажата", () =>
            {
                int t = w.FirstNamedMacroOtherThan(demo);
                w.DemoRepeat(t, MacroRepeatMode.Count, 65535); Render(w, 2);
                w.DemoRepeat(t, MacroRepeatMode.Toggle, 1); Render(w, 2);
                w.DemoRepeat(t, MacroRepeatMode.WhileHeld, 1); Render(w, 2);
            });
            for (int s = 0; s < MainWindow.WelcomeSlideCount; s++) { int ss = s; Step($"{th}: приветствие {ss + 1}", () => { w.ShowWelcome(true, ss); Render(w, 0); }); }
            Step($"{th}: закрыть приветствие", () => w.ShowWelcome(false));
            Step($"{th}: диалог «клавиша занята»", () => Dialog(w.BuildBusyKeyDialog(0x68, w.HostUseOf(0x68) ?? "Кольцо действий", out _)));
            Step($"{th}: диалог «проверка памяти»", () =>
            {
                var img = (byte[])MainWindow.DefaultImage.Clone(); img[0xD0] = 0xDA; img[0xD4] = 0xFF;
                Dialog(w.BuildLayoutDialog(K13Header.Inspect(img, 0xE0), @"C:\backups\k13-prewrite.bin", out _));
            });
            // v5.7: calmer UI — technical details hidden by default, rare options folded, rare step types under «Ещё»
            Step($"{th}: v5.7 технические подробности скрыты", () =>
            {
                if (w.ShowTech) throw new Exception("по умолчанию подробности включены");
                w.ShowPage(2); Pump();
                if (w.MemText.Visibility == Visibility.Visible) throw new Exception("байты памяти макросов видны");
                if (w.SafeText.Visibility == Visibility.Visible) throw new Exception("служебная строка внизу видна");
                var img = (byte[])MainWindow.DefaultImage.Clone(); img[0xD0] = 0xDA;
                var d = w.BuildLayoutDialog(K13Header.Inspect(img, 0xE0), null, out _);
                if (AllText(d).Contains("0xD0")) throw new Exception("адрес 0xD0 в диалоге без подробностей");
                w.SetShowTech(true); Pump();
                if (w.MemText.Visibility != Visibility.Visible || w.SafeText.Visibility != Visibility.Visible) throw new Exception("подробности не показались");
                var d2 = w.BuildLayoutDialog(K13Header.Inspect(img, 0xE0), null, out _);
                if (!AllText(d2).Contains("0xD0")) throw new Exception("нет адреса 0xD0 с подробностями");
                w.SetShowTech(false); Pump();
                if (w.MemText.Visibility == Visibility.Visible) throw new Exception("подробности не скрылись");
            });
            Step($"{th}: v5.7 «Добавить шаг» — 4 частых + «Ещё»", () =>
            {
                w.ShowPage(2); Pump();
                var t = AllText(w.MacroAddBar);
                foreach (var need in new[] { "Клавиша", "Текст", "Пауза", "Щелчок", "Ещё" }) if (!t.Contains(need)) throw new Exception("нет кнопки " + need);
                if (t.Contains("Прокрутка") || t.Contains("Отпустить")) throw new Exception("редкие шаги на виду");
            });
            Step($"{th}: v5.7 выбор программы — заголовок окна свёрнут", () =>
            {
                var d = w.BuildAppPicker(w.ProfileByName("Игры")!);
                d.Owner = null; d.Left = -32000; d.Top = -32000; d.ShowActivated = false; d.WindowStartupLocation = WindowStartupLocation.Manual; d.Show(); Pump(); d.UpdateLayout();
                var cb = Find<CheckBox>(d).FirstOrDefault(c => (c.Content as string)?.StartsWith("Только если") == true) ?? throw new Exception("нет условия по заголовку");
                if (cb.IsVisible) throw new Exception("условие по заголовку не свёрнуто");
                if (!AllText(d).Contains("Дополнительно")) throw new Exception("нет «Дополнительно»");
                d.Close();
            });
            // v5.6.4: read passes disagree → dialog with «Сохранить диагностику» / «Открыть папку»; F14–F24 hidden from the key list
            Step($"{th}: диалог «проходы чтения расходятся»", () =>
            {
                var a = new byte[16]; var b = new byte[16]; a[0] = 0xBA; b[0] = 0xFF;
                var ex = new K13ReadMismatchException(a, b, new[] { 0 }, "тест");
                ex.SavedFiles.AddRange(new[] { @"C:\backups\k13-readpass-A-20261116-133000.bin", @"C:\backups\k13-readpass-B-20261116-133000.bin" });
                Dialog(w.BuildWriteErrorDialog(ex, out _));
                if (!Directory.Exists(BackupStore.DefaultFolder)) throw new Exception("папка резервных копий не создана");
            });
            Step($"{th}: список клавиш без F14–F24", () =>
            {
                w.ShowPage(0); w.SelectControl(3); w.OpenTile("key");
                var names = w.KeyListNames();
                if (!names.Contains("F13") || !names.Contains("F12")) throw new Exception("нет F12/F13 в списке");
                if (names.Any(n => n is "F14" or "F18" or "F24")) throw new Exception("F14–F24 в списке клавиш");
                w.OpenTile(null); w.SelectControl(-1);
            });
            // v5.4: auto-switch, program picker, export / import
            Step($"{th}: автопереключение — демо-правила", () => { w.DemoV54(); w.ScrollProfiles(0); Render(w, 3); w.ScrollProfiles(400); Render(w, 3); });
            Step($"{th}: автопереключение — пауза и выключение", () => { w.SetAutoPaused(true); Render(w, 3); w.SetAutoPaused(false); });
            Step($"{th}: выбор программы", () => Dialog(w.BuildAppPicker(w.ProfileByName("Игры")!)));
            Step($"{th}: выбор программы — проверки", () =>
            {
                var p = w.ProfileByName("Игры")!;
                if (w.AddRule(p, "", "") == null) throw new InvalidOperationException("пустое правило принято");
                if (w.AddRule(p, "VorotexK13Studio.exe", "") == null) throw new InvalidOperationException("своё окно принято");
                if (w.AddRule(p, "a|b", "") == null) throw new InvalidOperationException("недопустимые символы приняты");
                if (w.AddRule(p, @"C:\Games\Witcher3.exe", "") != null) throw new InvalidOperationException("путь к exe не принят");
                if (w.AddRule(p, "witcher3", "") == null) throw new InvalidOperationException("повтор принят");
                if (w.AddRule(w.ProfileByName("Фото")!, "witcher3", "") != null) throw new InvalidOperationException("перенос не сработал");
                Pump(); Render(w, 3);
            });
            Step($"{th}: экспорт — все профили", () => Dialog(w.BuildExportDialog(w.AllProfiles())));
            Step($"{th}: экспорт — один профиль", () => Dialog(w.BuildExportDialog(w.AllProfiles().Take(1))));
            Step($"{th}: импорт — просмотр с конфликтом", () => Dialog(w.BuildImportDialog(w.DemoImportPreview(), out _)));
            Step($"{th}: импорт — сохранить оба и заменить", () =>
            {
                int before = w.AllProfiles().Count;
                var names = w.ApplyImport(w.DemoImportPreview(), ImportConflict.KeepBoth);
                if (w.AllProfiles().Count != before + 2 || !names.Contains("Фото (2)")) throw new InvalidOperationException("сохранить оба: " + string.Join(", ", names));
                w.ApplyImport(w.DemoImportPreview(), ImportConflict.Replace);
                if (w.AllProfiles().Count != before + 2) throw new InvalidOperationException("замена добавила профили");
                Render(w, 3);
            });
            Step($"{th}: импорт — плохие данные отклоняются", () =>
            {
                foreach (var bad in new[] { "", "not json", "{\"Format\":\"vorotex-k13-profile\",\"FormatVersion\":9,\"Profiles\":[{}]}", "{\"Name\":\"x\",\"Device\":{\"Base\":5}}" })
                {
                    try { ProfileExchange.Read(bad, MainWindow.DefaultImage, Array.Empty<string>()); throw new InvalidOperationException("принято: " + bad); }
                    catch (ProfileImportException) { }
                }
            });
            Step($"{th}: перетаскивание — подсказка", () => { w.ShowDropOverlay(true); Render(w, 3); w.ShowDropOverlay(false); });
            // v5.5: «profile switched» notice
            Step($"{th}: уведомление — профиль «1С» и настройки", () => { w.DemoV55(); w.ScrollProfiles(0); Render(w, 3); });
            Step($"{th}: уведомление — окно и замена без наложения", () =>
            {
                var t = w.ShowToastPreview("по программе 1cv8c.exe");
                Pump(); t.UpdateLayout();
                if (!t.IsVisible || t.ShowActivated || t.ShowInTaskbar || !t.Topmost) throw new InvalidOperationException("окно уведомления: видно/фокус/панель задач/поверх");
                var root = (FrameworkElement)VisualTreeHelper.GetChild(t, 0);
                if (root.ActualWidth < 200 || root.ActualHeight < 50) throw new InvalidOperationException($"уведомление слишком маленькое: {root.ActualWidth}×{root.ActualHeight}");
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); rtb.Render(root);
                int shown = t.Shown, replaced = t.Replaced;
                var t2 = w.ShowToastPreview("по умолчанию (chrome.exe — без правила)");
                if (!ReferenceEquals(t, t2) || t.Shown != shown || t.Replaced != replaced + 1) throw new InvalidOperationException($"второе уведомление не заменило первое: показано {t.Shown}, заменено {t.Replaced}");
                if (t.Current?.Detail.StartsWith("по умолчанию") != true) throw new InvalidOperationException("текст не обновился");
                t.HideNow();
                if (t.IsVisible) throw new InvalidOperationException("не скрылось");
            });
            Step($"{th}: уведомление — карточка для отчёта", () =>
            {
                var c = w.ToastCard("Основной", "");
                c.Measure(new Size(800, 400)); c.Arrange(new Rect(c.DesiredSize));
                if (c.DesiredSize.Width < 200) throw new InvalidOperationException("карточка без текста слишком узкая");
            });
            Step($"{th}: уведомление — места на экране и выключение", () =>
            {
                foreach (var pl in Enum.GetValues<ToastPlacement>()) { w.SetToastSettings(placement: pl); w.ShowToastPreview().HideNow(); }
                w.SetToastSettings(enabled: false, placement: ToastPlacement.BottomRight); Render(w, 3);
                w.SetToastSettings(enabled: true);
            });
            // v5.6: calmer auto-switch card
            Step($"{th}: автопереключение — подсказка и меню ⋯", () =>
            {
                w.SetAutoSwitchHintSeen(false); Render(w, 3);
                w.SetAutoSwitchHintSeen(true); Render(w, 3);
                var m = w.ToastMenu(w);
                if (m.Items.Count != 2) throw new InvalidOperationException("в меню ⋯ ожидалось 2 пункта, а не " + m.Items.Count);
            });
            // v5.7.2: v5.7 cards, slim header (auto-switch + «Новый» + ⋯), one line under the cards
            Step($"{th}: профили — тонкая шапка, меню ⋯ и строка внизу", () =>
            {
                Render(w, 3);
                if (w.ProfileCardCount != w.AllProfiles().Count) throw new InvalidOperationException($"карточек {w.ProfileCardCount}, профилей {w.AllProfiles().Count}");
                var tags = w.ProfilesMenu(w).Items.OfType<MenuItem>().Select(i => i.Tag as string).ToList();
                foreach (var t in new[] { "fallback", "toast", "pcmode" })
                    if (!tags.Contains(t)) throw new InvalidOperationException("в ⋯ профилей нет пункта " + t);
                var toast = w.ProfilesMenu(w).Items.OfType<MenuItem>().First(i => (i.Tag as string) == "toast");
                if (toast.Items.Count < 6) throw new InvalidOperationException("подменю уведомления: " + toast.Items.Count);
                var main = AllText(w.MainMenu());
                if (!main.Contains("Сворачивать в трей") || !main.Contains("Запускать вместе с Windows") || !main.Contains("тема")) throw new InvalidOperationException("трей/автозапуск/тема не в главном меню ⋯");
                if (w.TrayBoxForTest.IsVisible) throw new InvalidOperationException("карточка «Настройки» осталась на странице");
                var all = AllText(w);
                if (!all.Contains("Действия на компьютере") || !all.Contains("Автопереключение")) throw new InvalidOperationException("нет строки действий на ПК или переключателя");
                if (all.Contains("В других программах") || all.Contains("Уведомление при смене")) throw new InvalidOperationException("редкие настройки остались в шапке");
            });
            // v5.7.3: replug hint after a write + «factory layout» banner
            Step($"{th}: подсказка «переподключите» и предупреждение о заводской раскладке", () =>
            {
                var dlg = w.WrittenHintDialog(1.2, out _);
                var t = AllText(dlg);
                if (!t.Contains("режиме Normal") || !t.Contains("синий индикатор") || !t.Contains("Сохранить диагностику")) throw new InvalidOperationException("нет подсказки про режимы Normal/Game");
                dlg.Close();
                Render(w, 0);
                w.SimulateForeignKey(0x61);
                try
                {
                    Render(w, 0);
                    if (!AllText(w).Contains("в режиме Normal")) throw new InvalidOperationException("нет предупреждения о режиме Normal");
                    // 1.0.1: no manual switch button; the banner says to press the stick and offers «Запоминать режим…» while it is off
                    if (AllText(w).Contains(K13ModeHints.RemovedManualAction)) throw new InvalidOperationException("в предупреждении осталась кнопка «Включить Game сейчас»");
                    if (!AllText(w).Contains("нажмите центр стика")) throw new InvalidOperationException("в предупреждении нет подсказки «нажмите центр стика»");
                    if (K13ModeHints.OfferRemember(w.RememberState.Value) != AllText(w).Contains(K13ModeHints.RememberAction)) throw new InvalidOperationException("кнопка «Запоминать режим…» в предупреждении не соответствует флагу");
                }
                finally { w.SimulateForeignKey(0); }
            });
            // v5.7.5: stick tilts are shown and never empty
            Step($"{th}: наклоны стика — стрелки по умолчанию и видны в редакторе", () =>
            {
                w.ShowPage(0); w.SelectControl(15); Render(w, 0);
                var t = AllText(w);
                if (!t.Contains("Наклоны: ↑ ↑ · ↓ ↓ · ← ← · → →")) throw new InvalidOperationException("в редакторе стика нет строки наклонов со стрелками");
                w.SelectControl(-1);
            });
            // v5.7.4: activation setting + bisect test in the main ⋯ menu
            Step($"{th}: меню ⋯ — включение Game и тест команды", () =>
            {
                var main = AllText(w.MainMenu());
                if (main.Contains(K13ModeHints.RemovedManualAction)) throw new InvalidOperationException("в меню остался пункт «Включить Game сейчас»");
                foreach (var t in new[] { K13ModeHints.ConnectSetting, "Восстановить из резервной копии" })
                    if (!main.Contains(t)) throw new InvalidOperationException("нет пункта " + t);
                // v5.10: diagnostic tools only with «технические подробности»
                bool tech = w.ShowTech;
                w.SetShowTech(false);
                if (AllText(w.MainMenu()).Contains("Тест: какая команда включает Game")) throw new InvalidOperationException("тест команды виден без технических подробностей");
                w.SetShowTech(true);
                if (!AllText(w.MainMenu()).Contains("Тест: какая команда включает Game")) throw new InvalidOperationException("нет теста команды в технических подробностях");
                w.SetShowTech(tech);
                // the Game setting starts its own group (after a separator)
                var items = w.MainMenu().Items.Cast<object>().ToList();
                int set = items.FindIndex(i => i is MenuItem mi && AllText(mi).Contains(K13ModeHints.ConnectSetting));
                if (set < 1 || items[set - 1] is not Separator) throw new InvalidOperationException($"настройка Game не отделена чертой ({set})");
            });
            // v5.9: experimental «remember mode» — menu item next to the Game items, dialog with explanation, nothing written
            Step($"{th}: меню ⋯ — «Запоминать режим» и окно", () =>
            {
                var items = w.MainMenu().Items.Cast<object>().ToList();
                int set = items.FindIndex(i => i is MenuItem mi && AllText(mi).Contains(K13ModeHints.ConnectSetting));
                if (set < 0 || items.Count <= set + 1 || items[set + 1] is not MenuItem rm || !AllText(rm).Contains("Запоминать режим:") || AllText(rm).Contains("эксперимент"))
                    throw new InvalidOperationException("нет пункта «Запоминать режим» после настройки Game");
                if (!AllText(rm).Contains("Запоминать режим:")) throw new InvalidOperationException("в пункте меню нет состояния");
                // v5.9.2: OFF → «Включить» is the default; ON → «Готово» is the default, disabling is a separate left button
                Button? Default(Window d) => Find<Button>(d).FirstOrDefault(b => b.IsDefault);
                var offDlg = w.RememberDialog(0x91, true, out var offAns);
                var t0 = AllText(offDlg);
                foreach (var must in new[] { "Сейчас: ВЫКЛЮЧЕНО", "и Normal, и Game", "0x000F", "синий индикатор", K13ModeHints.ConnectSetting, "0x0000–0x0007", "Включить запоминание", "Восстановить из копии…", "Закрыть" })
                    if (!t0.Contains(must)) throw new InvalidOperationException($"в окне (выкл) нет «{must}»");
                if (t0.Contains("Выключить запоминание")) throw new InvalidOperationException("в выключенном состоянии не должно быть «Выключить»");
                if (Default(offDlg)?.Content as string != "Включить запоминание") throw new InvalidOperationException("в выключенном состоянии по умолчанию должно быть «Включить»");
                if (offAns() != MainWindow.RememberChoice.Close) throw new InvalidOperationException("без нажатия окно должно ничего не делать");
                Dialog(offDlg); offDlg.Close();
                var onDlg = w.RememberDialog(0x93, true, out var onAns);
                var t1 = AllText(onDlg);
                foreach (var must in new[] { "Сейчас: ВКЛЮЧЕНО", "Готово", "Выключить запоминание…", "Ничего нажимать не нужно" })
                    if (!t1.Contains(must)) throw new InvalidOperationException($"в окне (вкл) нет «{must}»");
                if (t1.Contains("Включить запоминание")) throw new InvalidOperationException("во включённом состоянии не должно быть «Включить»");
                var defOn = Default(onDlg);
                if (defOn?.Content as string != "Готово" || !defOn.IsCancel) throw new InvalidOperationException("во включённом состоянии по умолчанию должно быть «Готово»");
                if (Find<Button>(onDlg).First(b => b.Content as string == "Выключить запоминание…").IsDefault) throw new InvalidOperationException("«Выключить» не должна быть кнопкой по умолчанию");
                Dialog(onDlg); onDlg.Close();
                var conf = MainWindow.DisableConfirm(null, 0x93, out var confAns);
                if (!AllText(conf).Contains("Клавиатура снова будет стартовать в Normal") || confAns() != MessageBoxResult.No) throw new InvalidOperationException("подтверждение выключения должно по умолчанию отвечать «нет»");
                conf.Close();
                if (t0.Contains("ЭКСПЕРИМЕНТ") || t0.Contains("эксперимент")) throw new InvalidOperationException("в окне осталось «эксперимент»");
                if (!MainWindow.RememberEnabledNext.Contains("Это окно больше открывать не нужно")) throw new InvalidOperationException("нет подсказки после включения");
                var off = w.RememberDialog(null, false, out _);
                if (AllText(off).Contains("Включить запоминание") || !AllText(off).Contains("НЕИЗВЕСТНО")) throw new InvalidOperationException("без клавиатуры не должно быть кнопки записи");
                off.Close();
                var odd = w.RememberDialog(0x55, true, out _);
                if (!AllText(odd).Contains("НЕЗНАКОМОЕ") || AllText(odd).Contains("Включить запоминание") || AllText(odd).Contains("Выключить запоминание")) throw new InvalidOperationException("незнакомое значение должно блокировать запись");
                odd.Close();
                if (!w.DeviceInfo().Contains("EEPROM 0x000F")) throw new InvalidOperationException("нет 0x000F в сведениях об устройстве");
            });
            // v5.8: stick press — factory Normal/Game toggle or the ring, with an honest note
            Step($"{th}: нажатие стика — выбор Normal/Game или кольцо", () =>
            {
                w.ShowPage(0); w.SelectControl(16); Render(w, 0);
                byte keep = w.Cfg.GetKey(16);
                var t = AllText(w);
                if (!t.Contains("Переключение Normal/Game (заводское)") || !t.Contains("Кольцо действий")) throw new InvalidOperationException("нет выбора для нажатия стика");
                w.SetStickPress(ring: false); Render(w, 0);
                if (!AllText(w).Contains("переключение Normal / Game")) throw new InvalidOperationException("стик не переключился на заводское Normal/Game");
                w.SetStickPress(ring: true); Render(w, 0);
                if (!AllText(w).Contains("вернуться в Normal можно лишь переподключением")) throw new InvalidOperationException("нет честного предупреждения о кольце на стике");
                // v5.8.1: the ring tile on the stick takes F13 (was F14: the prepared F13 ring binding counted as busy)
                w.SetStickPress(ring: false); w.SelectControl(16);
                if (w.AssignRingToSelected() is { } err) throw new InvalidOperationException(err);
                if (w.Cfg.GetKey(16) != 0x68) throw new InvalidOperationException($"кольцо на стике получило 0x{w.Cfg.GetKey(16):X2} вместо F13");
                Render(w, 0);
                if (!AllText(w).Contains("Кольцо действий (F13)")) throw new InvalidOperationException("подсказка стика не показывает F13");
                w.Cfg.SetKey(16, keep); w.Changed();
                w.SelectControl(-1);
                if (!w.DeviceInfo().Contains("Режим:")) throw new InvalidOperationException("нет режима в сведениях об устройстве");
            });
            // v5.6.1: «?» help — hover tooltip + click popup; no ToolTip with StaysOpen=false anywhere (throws on real Windows)
            Step($"{th}: «?» открывается кликом, подсказки без StaysOpen=false", () =>
            {
                foreach (var page in new[] { 0, 1, 2, 3 })
                {
                    Render(w, page);
                    var bad = ForbiddenToolTips(w).ToList();
                    if (bad.Count > 0) throw new InvalidOperationException("ToolTip со StaysOpen=false: " + string.Join(", ", bad));
                }
                Render(w, 3);
                var dots = Tree(w).OfType<Button>().Where(b => b.GetValue(MainWindow.AutomationHelpKey) is string).ToList();
                if (dots.Count < 2) throw new InvalidOperationException("кнопок «?» найдено " + dots.Count);
                foreach (var d in dots)
                {
                    if (d.ToolTip is ToolTip) throw new InvalidOperationException("«?»: ToolTip-объект вместо простого содержимого");
                    var pop = (System.Windows.Controls.Primitives.Popup)d.Tag;
                    d.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    if (!pop.IsOpen || pop.StaysOpen) throw new InvalidOperationException("«?» не открыл всплывающее окно");
                    pop.IsOpen = false;
                }
            });
            // v5.6: keys through the PC
            Step($"{th}: кнопки через ПК — демо и вкладка «Кнопки»", () =>
            {
                w.DemoV56(); w.DemoPcLayer(true);
                if (!w.LayerState.Active || w.LayerState.Keys.Count != 11) throw new InvalidOperationException("план: " + w.LayerState.Keys.Count);
                w.DemoKeypadHolds("Основной");
                w.ShowPage(0); w.SelectControl(9); Render(w, 0);
                w.SelectControl(16); Render(w, 0); w.SelectControl(-1);
                if (!PcLayerPlanner.LooksPrepared(w.LayerState, K13MemoryCodec.Decode(K13MemoryCodec.Encode(w.DeviceTarget, MainWindow.DefaultImage)))) throw new InvalidOperationException("цель записи не подготовлена");
                Render(w, 3);
            });
            Step($"{th}: через ПК — «Запись не нужна» и клавиатура прислала Tab", () =>
            {
                w.DemoPcLayer(true);
                w.DemoKeypadHolds("Основной");
                w.ShowPage(0); Render(w, 0);
                if (!w.WriteNoteShown.StartsWith("Запись не нужна")) throw new InvalidOperationException("подпись у «Записать»: «" + w.WriteNoteShown + "»");
                if (w.ModeBannerWarns) throw new InvalidOperationException("предупреждение без причины");
                w.OnKeypadKey(0x09, 0x0F, true);                       // the keypad sent a plain Tab instead of F23
                if (!w.ModeBannerWarns) throw new InvalidOperationException("нет предупреждения «клавиатура прислала Tab»");
                Render(w, 0);
                w.OnLayerRouted(9, 0x86, "Основной", PcLayerPlanner.Resolve(w.DeviceTarget, 9));
                if (w.ModeBannerWarns) throw new InvalidOperationException("предупреждение не снялось после кода F23");
                w.DemoPcLayer(false);
                if (w.WriteNoteShown.Length > 0) throw new InvalidOperationException("подпись в обычном режиме");
            });
            Step($"{th}: кнопки через ПК — расхождение и диалог подготовки", () =>
            {
                w.DemoPcLayer(false);
                w.DemoKeypadHolds("Основной");
                w.ShowPage(0); w.SelectControl(9); Render(w, 0); w.SelectControl(-1);
                Dialog(w.BuildPrepareDialog());
                w.DemoKeypadHolds(null);
            });
            // v5.9.5: K13 keys the hook never got (Raw Input fallback) and the «окно от администратора» banner
            Step($"{th}: v5.9.5 кнопка мимо перехватчика и окно от администратора", () =>
            {
                w.DemoPcLayer(true);
                foreach (var (vk, dn) in new (ushort, bool)[] { (0x91, true), (0x79, true), (0x79, false), (0x91, false) }) w.OnKeypadRaw(vk, (ushort)0, dn);
                w.DemoHookBlocked("game");
                if (!w.ModeBannerWarns || !w.ModeBannerText.Contains("запустите Studio от имени администратора", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("нет предупреждения про окно от администратора: " + w.ModeBannerText);
                Render(w, 0);
                w.DemoHookBlocked(null);
                if (w.ModeBannerText.Contains("администратора")) throw new InvalidOperationException("предупреждение не снялось");
                w.DemoPcLayer(false);
            });
            // v5.9.8: real hold through the PC — the «пока зажата» key gets the plain code «Меню» (real release) after «Записать»
            Step($"{th}: v5.9.8 «пока зажата» через ПК — код «Меню» с настоящим отпусканием", () =>
            {
                w.DemoV56(); w.DemoPcLayer(true); w.DemoKeypadHolds(null);
                byte tabWas = w.KeyAt(9);
                int s = w.SlotOnKey(9);
                if (s < 0) s = w.DemoMacroOnKey(9);                    // earlier steps may have put another key on Tab
                w.DemoRepeat(s, MacroRepeatMode.WhileHeld, 1); Render(w, 2);
                if (!w.HoldCols().Contains(9)) throw new InvalidOperationException("Tab не попал в кнопки с удержанием");
                var kpImg = K13MemoryCodec.Decode(K13MemoryCodec.Encode(w.DeviceTarget, MainWindow.DefaultImage));
                var planned = w.HoldColsPlanned();
                if (planned.Count != 1 || planned.Any(p => kpImg.GetKey(p.Key) != p.Value || p.Value != PcLayerPlanner.HoldPool[0]))
                    throw new InvalidOperationException($"код «Меню» в цели записи неверен: удержание [{string.Join(",", w.HoldCols())}], коды [{string.Join(",", planned.Select(x => x.Key + ":" + x.Value))}]");
                if (!planned.ContainsKey(9) && kpImg.GetKey(9) == PcLayerPlanner.HoldPool[0]) throw new InvalidOperationException("код «Меню» у двух кнопок");
                if (!MainWindow.HoldHint(true, MainWindow.HoldWhere.PcHold).Contains("Через ПК, по удержанию") || !MainWindow.MouseHint(2, MainWindow.HoldWhere.PcHold).Contains("отпустили — перестала"))
                    throw new InvalidOperationException("нет подсказки «по удержанию»");
                if (kpImg.Macros.Any(m => m.Mode1Bit)) throw new InvalidOperationException("в цели записи остался бит «пока зажата» у макроса-сигнала");
                if (!PcLayerPlanner.LooksPrepared(w.LayerState, kpImg)) throw new InvalidOperationException("цель записи с кодом «Меню» не считается подготовленной");
                bool tabHolds = planned.ContainsKey(9);
                if (!AllText(w).Contains(tabHolds ? MainWindow.NeedsWriteForHold : "только одна кнопка")) throw new InvalidOperationException("нет подсказки «нажмите «Записать»» / «только одна кнопка»");
                w.DemoKeypadHolds("Основной"); w.SelectMacro(s); Render(w, 2);
                if (w.HoldColsWritten().Count != 1 || w.HoldColsWritten().ContainsKey(9) != tabHolds) throw new InvalidOperationException("код «Меню» не виден в образе клавиатуры");
                if (!AllText(w).Contains(tabHolds ? "Через ПК, по удержанию" : "только одна кнопка")) throw new InvalidOperationException("подсказка у Tab не соответствует коду удержания");
                w.ShowPage(0); w.SelectControl(9); w.OpenTile("mouse"); Render(w, 0);
                if (AllText(w).Contains("старт / стоп") == tabHolds) throw new InvalidOperationException("подпись автоклика «старт / стоп» не соответствует коду удержания");
                w.OpenTile(null); w.SelectControl(-1);
                w.DemoRepeat(s, MacroRepeatMode.Count, 1); w.DemoKeypadHolds(null); w.DemoPcLayer(false);
                if (w.KeyAt(9) != tabWas) w.DemoSetKey(9, tabWas);
            });
            // v5.9.7: the safety stop — a «пока зажата» play fed by keypad taps stops by itself when the taps stop
            Step($"{th}: v5.9.7 страховка автоклика без сигналов K13 (автоповтор)", () =>
            {
                if (MacroPlayer.WatchdogMs < 900) throw new InvalidOperationException("страховка короче медленного автоповтора клавиш");
                var m = new MacroSlot { RepeatMode = MacroRepeatMode.WhileHeld, Events = { new() { Down = true, Code = 0, Delay = 2 }, new() { Down = false, Code = 0, Delay = 2 } } };
                MacroPlayer.PlayWhileHeld(m); MacroPlayer.KeepAlive();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (MacroPlayer.IsPlaying && sw.ElapsedMilliseconds < 3000) System.Threading.Thread.Sleep(20);
                if (MacroPlayer.IsPlaying) { MacroPlayer.Stop(); throw new InvalidOperationException("макрос играет без сигналов дольше 3 с"); }
                if (sw.ElapsedMilliseconds < MacroPlayer.WatchdogMs - 50) throw new InvalidOperationException($"остановился слишком рано: {sw.ElapsedMilliseconds} мс");
                // without taps (toggle / legacy key-up scheme) the watchdog stays off
                MacroPlayer.PlayWhileHeld(m); System.Threading.Thread.Sleep(MacroPlayer.WatchdogMs + 300);
                bool still = MacroPlayer.IsPlaying; MacroPlayer.Stop();
                if (!still) throw new InvalidOperationException("страховка сработала без сигналов клавиатуры");
            });
            // v5.9.4: honest hold / autoclick hints — through the PC a hold is invisible (toggle), on the keypad «проверяется»
            Step($"{th}: v5.9.4 «пока зажата» и автоклик — через ПК и в клавиатуре", () =>
            {
                int t = w.FirstNamedMacroOtherThan(-1);
                w.DemoPcLayer(true);
                w.DemoRepeat(t, MacroRepeatMode.WhileHeld, 1); Render(w, 2);
                string pc = AllText(w);
                foreach (var must in new[] { "Через ПК", "работает как «До повторного нажатия»", "удержание программе не видно", MainWindow.NeedsWriteForHold })
                    if (!pc.Contains(must)) throw new InvalidOperationException($"через ПК, пока зажата: нет «{must}»");
                if (pc.Contains("Работает в самой клавиатуре")) throw new InvalidOperationException("осталось «Работает в самой клавиатуре»");
                w.DemoRepeat(t, MacroRepeatMode.Toggle, 1); Render(w, 2);
                if (!AllText(w).Contains("Через ПК: включены «Кнопки по профилю», макрос выполняет программа на ПК")) throw new InvalidOperationException("через ПК, до повторного: нет подсказки");
                w.ShowPage(0); w.SelectControl(9); w.OpenTile("mouse"); Render(w, 0);
                string mpc = AllText(w);
                if (!mpc.Contains("старт / стоп") || !mpc.Contains("выключите «Кнопки по профилю (через ПК)» и нажмите «Записать»") || mpc.Contains("работает без программы."))
                    throw new InvalidOperationException("мышь через ПК: подсказка неверна");
                w.OpenTile(null); w.SelectControl(-1);
                w.DemoPcLayer(false);
                w.DemoRepeat(t, MacroRepeatMode.WhileHeld, 1); Render(w, 2);
                string kp = AllText(w);
                if (!kp.Contains("не останавливает повтор") || kp.Contains("Через ПК: включены")) throw new InvalidOperationException("в клавиатуре: нет честной подсказки про удержание");
                w.ShowPage(0); w.SelectControl(9); w.OpenTile("mouse"); Render(w, 0);
                if (AllText(w).Contains("старт / стоп")) throw new InvalidOperationException("мышь в клавиатуре: подпись «старт / стоп»");
                if (!MainWindow.MouseHint(2, MainWindow.HoldWhere.KeypadNotWritten).Contains("не останавливает щелчки")) throw new InvalidOperationException("автоклик в клавиатуре без честной подсказки");
                w.OpenTile(null); w.SelectControl(-1);
                w.DemoRepeat(t, MacroRepeatMode.Count, 1);
            });
            // v5.10: «Действие на ПК» without «через ПК» goes to the keypad as the Scroll Lock + F chord, never as F14–F24
            Step($"{th}: v5.10 действие на ПК — сочетание Scroll Lock + F вместо F14–F24", () =>
            {
                w.DemoPcLayer(false);
                byte was = w.KeyAt(3);
                w.DemoSetKey(3, 0x69);
                var target = w.DeviceTarget;
                if (!KeyCatalog.IsMacro(target.GetKey(3))) throw new InvalidOperationException($"в цели записи у кнопки код 0x{target.GetKey(3):X2}, а не макрос-сочетание");
                var mac = target.Macros[KeyCatalog.MacroSlotOf(target.GetKey(3))];
                if (!mac.Events.Any(e => e.Code == 0x47) || !mac.Events.Any(e => e.Code == 0x3A)) throw new InvalidOperationException("макрос-сочетание не Scroll Lock + F1");
                for (int c = 0; c < K13Layout.KeymapColumns; c++) if (PcLayerPlanner.IsHostTrigger(target.GetKey(c))) throw new InvalidOperationException($"в цели записи остался F14–F24 у кнопки {c}");
                var rs = w.RoutedState;
                if (!rs.Keys.Any(k => k.Column == 3 && k.Code == 0x3A) || rs.Scheme != PcLayerState.ChordScheme) throw new InvalidOperationException("перехватчик не ждёт Scroll Lock + F1 для кнопки");
                w.DemoSetKey(3, was);
            });
            // 1.0.2: profiles with different lighting — after a switch the app says the keypad lighting differs and «Записать» is enabled
            Step($"{th}: 1.0.2 подсветка при смене профиля", () =>
            {
                string prev = w.DemoActiveProfile; bool wasLayer = w.LayerState.Active;
                w.DemoPcLayer(true);
                w.DemoLightingCopy("Основной", "Свет-смоук");
                w.DemoSwitchByUser("Основной");
                w.DemoKeypadHolds("Основной");
                w.DemoSwitchByUser("Свет-смоук");
                Render(w, 0);
                if (!w.DemoStatus.Contains("Подсветка в клавиатуре прежняя") || !w.DemoStatus.Contains("«Записать»")) throw new InvalidOperationException("после смены профиля нет подсказки про подсветку: «" + w.DemoStatus + "»");
                if (!w.WriteNoteShown.StartsWith("Нужно записать")) throw new InvalidOperationException("подпись у «Записать» после смены подсветки: «" + w.WriteNoteShown + "»");
                w.DemoSwitchByUser("Основной");
                if (w.DemoStatus.Contains("Подсветка")) throw new InvalidOperationException("подсказка про подсветку без причины: «" + w.DemoStatus + "»");
                w.DemoActivate(prev); w.DemoRemoveProfile("Свет-смоук"); w.DemoPcLayer(wasLayer); w.DemoKeypadHolds("Основной");
            });
            // 1.0.3: lighting on auto-switch — setting in the profiles ⋯ menu (on by default), auto switch offline gives an honest note
            Step($"{th}: 1.0.3 подсветка при автопереключении", () =>
            {
                var menu = w.ProfilesMenu(w);
                var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string == "autoled");
                if (item == null || !AllText(item).Contains("Менять подсветку при автопереключении")) throw new InvalidOperationException("нет пункта «Менять подсветку при автопереключении»");
                if (w.DemoAutoSwitchLighting != AllText(item).Contains("\uE73E")) throw new InvalidOperationException("галочка не соответствует настройке");
                bool was = w.DemoAutoSwitchLighting;
                w.SetAutoSwitchLighting(!was); if (w.DemoAutoSwitchLighting == was) throw new InvalidOperationException("настройка не переключилась");
                w.SetAutoSwitchLighting(was);
                string prev = w.DemoActiveProfile; bool wasLayer = w.LayerState.Active;
                w.DemoPcLayer(true);
                w.DemoLightingCopy("Основной", "Свет-авто");
                w.DemoSwitchByUser("Основной");
                w.DemoKeypadHolds("Основной");
                w.DemoSwitchAuto("Свет-авто");
                if (!w.DemoStatus.Contains("Подсветка в клавиатуре прежняя")) throw new InvalidOperationException("после автопереключения (без клавиатуры) нет подсказки: «" + w.DemoStatus + "»");
                w.DemoActivate(prev); w.DemoRemoveProfile("Свет-авто"); w.DemoPcLayer(wasLayer); w.DemoKeypadHolds("Основной");
            });
            // v5.10: restore from a backup = a new profile, the keypad changes only via «Записать»
            Step($"{th}: v5.10 восстановление из резервной копии", () =>
            {
                if (!AllText(w.MainMenu()).Contains("Восстановить из резервной копии")) throw new InvalidOperationException("нет пункта «Восстановить из резервной копии»");
                string prev = w.DemoActiveProfile;
                var f = Path.Combine(Path.GetTempPath(), "k13-smoke-restore.bin");
                File.WriteAllBytes(f, MainWindow.DefaultImage);
                string name = w.RestoreFromBackup(f, ask: false);
                if (w.DemoActiveProfile != name || !name.StartsWith("Копия от ")) throw new InvalidOperationException($"копия не открылась профилем ({w.DemoActiveProfile})");
                Render(w, 0);
                if (!AllText(w).Contains("нажмите «Записать»")) throw new InvalidOperationException("нет подсказки «нажмите «Записать»» после восстановления");
                w.DemoActivate(prev); w.DemoRemoveProfile(name);
                File.Delete(f);
                bool threw = false;
                File.WriteAllBytes(f, new byte[100]);
                try { w.RestoreFromBackup(f, ask: false); } catch (K13FormatException) { threw = true; }
                File.Delete(f);
                if (!threw || w.AllProfiles().Any(p => p.Name.StartsWith("Копия от "))) throw new InvalidOperationException("испорченная копия не отклонена");
            });
            // v5.10: update check — notify only, can be switched off
            Step($"{th}: v5.10 проверка обновлений в меню ⋯", () =>
            {
                bool on = w.CheckUpdatesOn;
                var t = AllText(w.MainMenu());
                if (!t.Contains("Проверять обновления") || !t.Contains("Проверить обновления сейчас")) throw new InvalidOperationException("нет пунктов проверки обновлений");
                w.SetCheckUpdates(!on);
                if (w.CheckUpdatesOn == on) throw new InvalidOperationException("проверку обновлений не выключить");
                w.SetCheckUpdates(on);
                if (MainWindow.CurrentVersion < new Version(0, 5, 10)) throw new InvalidOperationException("версия сборки не поднята");
            });
            // v5.10.1: the expected keypad content is what «Записать» writes — no false warnings; stale ones clear on Game
            Step($"{th}: v5.10.1 нет ложных предупреждений, устаревшие снимаются", () =>
            {
                string prof = w.DemoActiveProfile;
                w.DemoPcLayer(false);
                byte was = w.KeyAt(0);
                w.DemoSetKey(0, 0x6A);                                            // PC action on key 1, «через ПК» off
                w.DemoKeypadHolds(prof);                                         // keypad = what «Записать» wrote (chord macro)
                w.ShowPage(0); w.SelectControl(0); Render(w, 0);
                if (w.ModeBannerText.Contains("делает не то") || w.EditorText().Contains("Сейчас в клавиатуре")) throw new InvalidOperationException("ложное предупреждение у действия на ПК: " + w.ModeBannerText);
                w.SelectControl(-1); w.DemoSetKey(0, was);
                // hold key with «Меню» in «через ПК»
                byte tabWas = w.KeyAt(9);
                int s = w.DemoMacroOnKey(9); w.DemoRepeat(s, MacroRepeatMode.WhileHeld, 1);
                w.DemoPcLayer(true); w.DemoKeypadHolds(prof);
                var hc = w.HoldColsWritten();
                if (hc.Count != 1) throw new InvalidOperationException("код «Меню» не записан в образ");
                w.ShowPage(0); w.SelectControl(hc.Keys.First()); Render(w, 0);
                if (w.EditorText().Contains("Клавиатура не подготовлена")) throw new InvalidOperationException("ложное «не подготовлена» у кнопки с «Меню»");
                if (w.ModeBannerWarns) throw new InvalidOperationException("ложное предупреждение вверху: " + w.ModeBannerText);
                w.SelectControl(-1);
                // Normal after replug: «Num 2» → Normal banner; Game → gone
                w.DemoHwMode(HwMode.Normal); w.DemoKeypadKey(0x62); Render(w, 0);
                if (!w.ModeBannerText.Contains("Normal")) throw new InvalidOperationException("в Normal нет объяснения: " + w.ModeBannerText);
                w.DemoHwMode(HwMode.Game); Render(w, 0);
                if (w.ModeBannerWarns) throw new InvalidOperationException("предупреждение осталось после перехода в Game: " + w.ModeBannerText);
                w.DemoHwMode(null);
                w.DemoRepeat(s, MacroRepeatMode.Count, 1); w.DemoKeypadHolds(null); w.DemoPcLayer(false);
                if (w.KeyAt(9) != tabWas) w.DemoSetKey(9, tabWas);
            });
            Step($"{th}: маленькое окно 960×640", () => { w.Width = 960; w.Height = 640; for (int p = 0; p < 4; p++) Render(w, p); });
            Step($"{th}: выход", () => w.ExitApp());
        }
        Step("кольцо на экране (отрисовка)", () =>
        {
            var ring = new RingView(true); ring.SetSlots(RingSettings.CreateDefault().Slots); ring.SetHighlightNow(2);
            ring.Measure(new Size(600, 600)); ring.Arrange(new Rect(0, 0, 600, 600));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 600, 96, 96, PixelFormats.Pbgra32); rtb.Render(ring);
        });
        lines.Insert(0, $"Шагов: {steps}, ошибок: {fails}, необработанных исключений: {Unhandled}");
        File.WriteAllLines(Path.Combine(folder, "smoke.txt"), lines);
        try { Directory.Delete(tmp + "light", true); Directory.Delete(tmp + "dark", true); } catch { }
        return fails;
    }

    private static void Render(MainWindow w, int page)
    {
        w.ShowPage(page);
        Pump();
        w.RenderFrame(1.3);
        w.UpdateLayout();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)root.ActualWidth), Math.Max(1, (int)root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
    }

    /// <summary>All texts (TextBlock, string content) in a logical tree.</summary>
    internal static string AllText(DependencyObject root)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(object o)
        {
            if (o is TextBlock tb) sb.AppendLine(tb.Text);
            if (o is ContentControl { Content: string str }) sb.AppendLine(str);
            if (o is DependencyObject d) foreach (var c in LogicalTreeHelper.GetChildren(d)) Walk(c);
        }
        Walk(root);
        return sb.ToString();
    }

    internal static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var c in LogicalTreeHelper.GetChildren(root))
            if (c is DependencyObject d)
            {
                if (d is T t) yield return t;
                foreach (var x in Find<T>(d)) yield return x;
            }
    }

    private static void Dialog(Window d)
    {
        d.Owner = null; d.Left = -32000; d.Top = -32000; d.ShowActivated = false; d.WindowStartupLocation = WindowStartupLocation.Manual;
        d.Show(); Pump(); d.UpdateLayout();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(d, 0);
        if (root.ActualWidth < 50 || root.ActualHeight < 50) throw new InvalidOperationException($"окно слишком маленькое: {root.ActualWidth}×{root.ActualHeight}");
        d.Close();
    }

    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
            Application.Current.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.Count > 0)
        {
            var o = stack.Pop(); yield return o;
            if (o is Visual || o is System.Windows.Media.Media3D.Visual3D)
                for (int i = VisualTreeHelper.GetChildrenCount(o) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(o, i));
        }
    }

    /// <summary>WPF on Windows throws InvalidOperationException when a ToolTip with StaysOpen=false is opened through
    /// the ToolTip property (Wine never hovers in the smoke test, so it never showed). Find any such tooltip.</summary>
    internal static IEnumerable<string> ForbiddenToolTips(DependencyObject root) =>
        Tree(root).OfType<FrameworkElement>()
            .Where(e => e.ToolTip is ToolTip t && !t.StaysOpen)
            .Select(e => e.GetType().Name + (string.IsNullOrEmpty(e.Name) ? "" : " " + e.Name));
}
