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
                    if (!AllText(w).Contains("Включить Game")) throw new InvalidOperationException("нет кнопки «Включить Game» в предупреждении");
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
                foreach (var t in new[] { "Включить режим Game (ваши назначения)", "Включать режим Game при подключении", "Тест: какая команда включает Game" })
                    if (!main.Contains(t)) throw new InvalidOperationException("нет пункта " + t);
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
