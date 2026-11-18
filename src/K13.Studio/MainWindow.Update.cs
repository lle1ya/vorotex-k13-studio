using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.10: «Проверять обновления» — asks GitHub once a day whether a newer release exists. Off switch: ⋯ → «Проверять обновления».
/// 1.1.0: «Скачать и обновить» — only after the user confirms: downloads the release file, checks SHA256SUMS.txt and the file itself,
/// exits cleanly (after any keypad exchange) and runs the silent installer / swaps the portable exe; the program then starts again.</summary>
public partial class MainWindow
{
    private UpdateCheck.Release? _newer;
    private bool _updateBusy;

    internal static Version CurrentVersion => typeof(App).Assembly.GetName().Version ?? new Version(0, 0);

    private void ScheduleUpdateCheck()
    {
        if (_offline) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        t.Tick += async (_, _) =>
        {
            t.Interval = TimeSpan.FromHours(6);
            if (UpdateCheck.AutoCheckDue(_store.Data.CheckUpdates, _store.Data.LastUpdateCheck, DateTime.UtcNow)) await CheckUpdatesAsync(manual: false);
        };
        t.Start();
    }

    internal async Task CheckUpdatesAsync(bool manual)
    {
        if (_updateBusy) return;
        if (!manual && !_store.Data.CheckUpdates) return;              // the setting wins over any timer
        _updateBusy = true;
        try
        {
            var o = await UpdateCheck.ResolveAsync(ApiLatestAsync, RedirectLatestAsync);
            Log("Обновления: " + o.Log);
            if (o.Latest is not { } r)
            {
                // nothing saved → the 6-hour timer tries again; no window for the automatic check
                if (manual) MessageBox.Show(this, o.Error ?? UpdateCheck.FailText, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _store.Data.LastUpdateCheck = DateTime.UtcNow; SaveStore();
            if (!UpdateCheck.IsNewer(r, CurrentVersion))
            {
                _newer = null;
                Log($"Обновления: установлена последняя версия ({UpdateCheck.Display(CurrentVersion)}).");
                if (manual) MessageBox.Show(this, $"У вас последняя версия ({UpdateCheck.Display(CurrentVersion)}).", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _newer = r;
            Log($"Обновления: на GitHub есть {r.Tag} (у вас {UpdateCheck.Display(CurrentVersion)}).");
            if (!manual && _store.Data.SkippedUpdate == r.Tag) return;
            OfferUpdate(r);
        }
        catch (Exception ex)
        {
            Log("Обновления: " + ex.Message);
            if (manual) MessageBox.Show(this, UpdateCheck.FailText, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _updateBusy = false; }
    }

    private static HttpClient NewHttp(bool redirects)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects }) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VorotexK13Studio/" + CurrentVersion);
        return http;
    }

    /// <summary>GitHub API (60 requests an hour per address — a shared office network can hit 403).</summary>
    private static async Task<(int, string?)> ApiLatestAsync()
    {
        using var http = NewHttp(redirects: true);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.GetAsync(UpdateCheck.ApiUrl);
        return ((int)resp.StatusCode, resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync() : null);
    }

    /// <summary>v5.10.2 fallback without the API: HEAD …/releases/latest, redirect not followed, tag from Location.</summary>
    private static async Task<(int, string?)> RedirectLatestAsync()
    {
        using var http = NewHttp(redirects: false);
        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, UpdateCheck.LatestPage));
        if ((int)head.StatusCode is >= 300 and < 400) return ((int)head.StatusCode, head.Headers.Location?.ToString());
        if ((int)head.StatusCode is 405 or 501)                       // HEAD not allowed → GET, still without following
        {
            using var get = await http.GetAsync(UpdateCheck.LatestPage, HttpCompletionOption.ResponseHeadersRead);
            return ((int)get.StatusCode, get.Headers.Location?.ToString());
        }
        return ((int)head.StatusCode, head.Headers.Location?.ToString());
    }

    private void OfferUpdate(UpdateCheck.Release r)
    {
        var kind = UpdateKind();
        var dlg = UpdateOfferDialog(this, r, kind, out var answer);
        dlg.ShowDialog();
        var a = answer();
        Log($"Обновления: предложено {r.Tag} ({(kind == UpdateInstall.Kind.Installed ? "установленная копия" : "переносная копия")}) — ответ: {a}.");
        if (a == MessageBoxResult.Yes) _ = DownloadAndUpdateAsync(r, kind);
        else if (a == MessageBoxResult.No) OpenReleasePage(r.Url);
        else { _store.Data.SkippedUpdate = r.Tag; SaveStore(); }
    }

    /// <summary>1.1.0: Yes = «Скачать и обновить», No = «Открыть страницу», Cancel = «Позже». Built separately for the smoke test.</summary>
    internal static Window UpdateOfferDialog(Window? owner, UpdateCheck.Release r, UpdateInstall.Kind kind, out Func<MessageBoxResult> answer) =>
        MessageBox.Build(owner is { IsLoaded: true, IsVisible: true } ? owner : null,
            $"Вышла новая версия VOROTEX K13 Studio: {r.Tag} (у вас {UpdateCheck.Display(CurrentVersion)}).\n\n" + UpdatePlanText(kind) +
            "\n\nПроверку можно выключить: ⋯ → «Проверять обновления».",
            "Обновление", MessageBoxButton.YesNoCancel, MessageBoxImage.Information, MessageBoxResult.Yes, out answer,
            new[] { "Скачать и обновить", "Открыть страницу", "Позже" });

    internal static string UpdatePlanText(UpdateInstall.Kind kind) => kind == UpdateInstall.Kind.Installed
        ? "«Скачать и обновить»: программа скачает установщик с GitHub, проверит контрольную сумму, закроется, установит новую версию поверх текущей и запустится снова. " +
          "Профили, назначения и резервные копии не затрагиваются (они хранятся отдельно, в %LOCALAPPDATA%\\VorotexK13Studio), ярлыки остаются. Права администратора не нужны."
        : "«Скачать и обновить»: это переносная копия (без установки). Программа скачает новый файл с GitHub, проверит контрольную сумму, " +
          "заменит им этот файл (под тем же именем, чтобы ваши ярлыки работали) и перезапустится. Профили и назначения не затрагиваются.";

    // ───────────── 1.1.0: «Скачать и обновить»

    /// <summary>Launch to do when the program actually exits (after any keypad exchange has finished — see ExitApp).</summary>
    private Func<bool>? _pendingUpdate;
    internal static string UpdatesFolder => Path.Combine(App.DataFolder, "updates");
    internal static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VorotexK13Studio.exe");

    /// <summary>Installed = the exe sits in the folder the installer recorded (HKCU uninstall key of our AppId); anything else is portable.</summary>
    internal static UpdateInstall.Kind UpdateKind()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UpdateInstall.UninstallKey);
            var loc = k?.GetValue("InstallLocation") as string;
            return UpdateInstall.IsInstalledCopy(Path.GetDirectoryName(ExePath)!, loc) ? UpdateInstall.Kind.Installed : UpdateInstall.Kind.Portable;
        }
        catch { return UpdateInstall.Kind.Portable; }
    }

    internal async Task DownloadAndUpdateAsync(UpdateCheck.Release r, UpdateInstall.Kind kind)
    {
        if (_pendingUpdate != null) return;
        string asset = UpdateInstall.Asset(kind, r.Tag);
        using var cts = new CancellationTokenSource();
        var (win, text, bar) = UpdateProgressWindow(this, $"Скачивание {r.Tag}", () => cts.Cancel());
        win.Show();
        Log($"Обновления: скачиваю {asset} ({r.Tag}).");
        string? file = null;
        try
        {
            Directory.CreateDirectory(UpdatesFolder);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VorotexK13Studio/" + CurrentVersion);

            text.Text = "Контрольные суммы…";
            string sumsUrl = UpdateInstall.DownloadUrl(r.Tag, UpdateInstall.SumsAsset);
            using (var sr = await http.GetAsync(sumsUrl, cts.Token))
            {
                if (sr.StatusCode == System.Net.HttpStatusCode.NotFound) { Log("Обновления: нет SHA256SUMS.txt у " + r.Tag); win.Close(); FailUpdate(UpdateInstall.NoSumsText, r); return; }
                sr.EnsureSuccessStatusCode();
                if (!UpdateInstall.IsAllowedDownloadHost(sr.RequestMessage?.RequestUri)) throw new IOException("ответ пришёл не с GitHub: " + sr.RequestMessage?.RequestUri?.Host);
                var sums = UpdateInstall.ParseSums(await sr.Content.ReadAsStringAsync(cts.Token));
                if (!sums.TryGetValue(asset, out var want)) { Log($"Обновления: в SHA256SUMS.txt нет {asset}"); win.Close(); FailUpdate(UpdateInstall.NoSumsText, r); return; }

                string url = UpdateInstall.DownloadUrl(r.Tag, asset);
                file = Path.Combine(UpdatesFolder, asset);
                string part = file + ".part";
                using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new IOException($"в выпуске {r.Tag} нет файла {asset}");
                    resp.EnsureSuccessStatusCode();
                    if (!UpdateInstall.IsAllowedDownloadHost(resp.RequestMessage?.RequestUri)) throw new IOException("файл пришёл не с GitHub: " + resp.RequestMessage?.RequestUri?.Host);
                    long? total = resp.Content.Headers.ContentLength;
                    if (total > UpdateInstall.MaxSize) throw new IOException("файл слишком большой");
                    await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
                    await using (var dst = File.Create(part))
                    {
                        var buf = new byte[81920]; long done = 0; int n; var last = DateTime.MinValue;
                        while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
                        {
                            await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
                            done += n;
                            if (done > UpdateInstall.MaxSize) throw new IOException("файл слишком большой");
                            if ((DateTime.Now - last).TotalMilliseconds > 150)
                            {
                                last = DateTime.Now;
                                text.Text = "Скачано " + UpdateInstall.ProgressText(done, total);
                                bar.IsIndeterminate = total is not > 0;
                                if (total is long t && t > 0) bar.Value = (double)done / t;
                            }
                        }
                    }
                    text.Text = "Проверка файла…"; bar.IsIndeterminate = true;
                    var data = await File.ReadAllBytesAsync(part, cts.Token);
                    var why = await Task.Run(() => UpdateInstall.CheckFile(kind, data, want, r.Version));
                    if (why != null)
                    {
                        TryDelete(part);
                        Log($"Обновления: файл {asset} отклонён — {why}.");
                        win.Close();
                        FailUpdate($"Скачанный файл не прошёл проверку: {why}. Программа его удалила и ничего не устанавливала.", r);
                        return;
                    }
                    TryDelete(file);
                    File.Move(part, file);
                    Log($"Обновления: {asset} скачан и проверен (SHA256 {want}, {data.LongLength:N0} байт) → {file}.");
                }
            }
            win.Close();
            ArmUpdate(kind, file, r);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Log("Обновления: скачивание отменено.");
            win.Close();
            if (file != null) TryDelete(file + ".part");
            StatusText.Text = "Обновление отменено.";
        }
        catch (Exception ex)
        {
            Log("Обновления: ошибка скачивания — " + ex);
            win.Close();
            if (file != null) TryDelete(file + ".part");
            FailUpdate("Не удалось скачать обновление: " + FriendlyError.Text(ex) + ". Ничего не установлено, текущая версия работает как прежде.", r);
        }
    }

    /// <summary>Ready: the program exits (waiting for any keypad exchange first), the launch happens in ExitApp.</summary>
    private void ArmUpdate(UpdateInstall.Kind kind, string file, UpdateCheck.Release r)
    {
        _pendingUpdate = kind == UpdateInstall.Kind.Installed ? () => LaunchInstaller(file, r) : () => SwapPortable(file, r);
        Log($"Обновления: всё готово, программа закроется для установки {r.Tag}" + (_busy ? " после завершения обмена с клавиатурой." : "."));
        StatusText.Text = _busy ? "Обновление: дождусь окончания обмена с клавиатурой и закроюсь…" : "Обновление: программа закрывается…";
        ExitApp();
    }

    /// <summary>Called by ExitApp before the hook, tray and timers are released. False = could not start: stay running.</summary>
    private bool RunPendingUpdate()
    {
        var go = _pendingUpdate; _pendingUpdate = null;
        if (go == null) return true;
        try { return go(); }
        catch (Exception ex)
        {
            Log("Обновления: не удалось запустить установку — " + ex);
            MessageBox.Show(this, "Не удалось запустить обновление: " + FriendlyError.Text(ex) + "\n\nПрограмма продолжает работать в прежней версии. Обновиться можно вручную: " + UpdateCheck.ReleasesPage,
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private bool LaunchInstaller(string setup, UpdateCheck.Release r)
    {
        string log = Path.Combine(UpdatesFolder, $"install-{r.Tag}.log");
        var args = UpdateInstall.InstallerArgs(log);
        Log($"Обновления: запускаю «{setup}» {args} (установщик дождётся выхода программы, затем запустит её снова).");
        using var p = Process.Start(new ProcessStartInfo(setup, args) { UseShellExecute = false, WorkingDirectory = UpdatesFolder });
        if (p == null) throw new IOException("установщик не запустился");
        App.FlushLog();
        return true;
    }

    private bool SwapPortable(string downloaded, UpdateCheck.Release r)
    {
        var plan = UpdateInstall.PlanSwap(ExePath, downloaded);
        Log($"Обновления: замена файла «{plan.Exe}» (старый → {Path.GetFileName(plan.Old)}).");
        UpdateInstall.Swap(plan, (a, b) => File.Move(a, b), File.Exists, File.Delete);
        try
        {
            using var p = Process.Start(new ProcessStartInfo(plan.Exe, $"--updated --wait-pid {Environment.ProcessId}") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Exe)! });
            if (p == null) throw new IOException("новая версия не запустилась");
        }
        catch
        {
            // put the old file back so nothing is left half-done
            try { File.Move(plan.Exe, downloaded, overwrite: true); File.Move(plan.Old, plan.Exe); } catch (Exception ex2) { Log("Обновления: откат не удался — " + ex2.Message); }
            throw;
        }
        App.FlushLog();
        return true;
    }

    private void FailUpdate(string text, UpdateCheck.Release r)
    {
        StatusText.Text = "Обновление не выполнено.";
        if (MessageBox.ShowCustom(this, text, "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes, "Открыть страницу выпуска", "Закрыть") == MessageBoxResult.Yes)
            OpenReleasePage(r.Url);
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    /// <summary>Progress window: text, bar, «Отмена». Closing it with ✕ also cancels.</summary>
    internal static (Window Win, TextBlock Text, ProgressBar Bar) UpdateProgressWindow(Window? owner, string title, Action cancel)
    {
        var win = Ui.MakeWindow(owner is { IsLoaded: true, IsVisible: true } ? owner : null, "Обновление", 440, 200);
        win.ResizeMode = ResizeMode.NoResize; win.SizeToContent = SizeToContent.Height;
        var sp = new StackPanel { Margin = new Thickness(24, 16, 24, 20) };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var text = new TextBlock { Text = "Подключение к GitHub…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, IsIndeterminate = true };
        var btn = new Button { Content = "Отмена", IsCancel = true, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(16, 7, 16, 7) };
        bool done = false;
        btn.Click += (_, _) => { if (!done) { done = true; cancel(); } };
        win.Closing += (_, _) => { if (!done) { done = true; cancel(); } };
        sp.Children.Add(text); sp.Children.Add(bar); sp.Children.Add(btn);
        win.Content = sp;
        return (win, text, bar);
    }

    /// <summary>1.1.0: «--updated» (set by the installer / the portable swap): say so and clean up; «--update-test-tag vX»: the
    /// whole «Скачать и обновить» flow against an existing release, even the same version (a way to test without a new release).</summary>
    private void AfterStartUpdateTasks(string[] args)
    {
        if (args.Contains("--updated"))
        {
            Log($"Обновления: запуск после обновления, версия {UpdateCheck.Display(CurrentVersion)} ({ExePath}).");
            StatusText.Text = $"Программа обновлена до {UpdateCheck.Display(CurrentVersion)}. Профили и назначения на месте.";
            _store.Data.SkippedUpdate = null; SaveStore();
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 10 && File.Exists(ExePath + ".old"); i++) { TryDelete(ExePath + ".old"); await Task.Delay(1000); }
                try { foreach (var f in Directory.EnumerateFiles(UpdatesFolder, "*.exe*")) TryDelete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            });
        }
        int t = Array.IndexOf(args, "--update-test-tag");
        if (t >= 0 && t + 1 < args.Length)
        {
            string tag = args[t + 1];
            var v = UpdateCheck.ParseTag(tag);
            if (v == null || !UpdateInstall.IsSafeTag(tag)) { Log("Обновления (тест): неверная метка " + tag); return; }
            Log($"Обновления (тест): проверка обновления на выпуске {tag} (у вас {UpdateCheck.Display(CurrentVersion)}).");
            OfferUpdate(new UpdateCheck.Release(tag, v, UpdateCheck.ReleasesPage + "/tag/" + tag, tag));
        }
    }

    private void OpenReleasePage(string url)
    {
        if (!UpdateCheck.IsReleaseUrl(url)) url = UpdateCheck.ReleasesPage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть браузер: " + FriendlyError.Text(ex) + "\n\n" + url, Title); }
    }

    internal void SetCheckUpdates(bool on)
    {
        _store.Data.CheckUpdates = on; SaveStore();
        Log(on ? "Проверка обновлений включена." : "Проверка обновлений выключена.");
        StatusText.Text = on ? "Программа будет раз в день проверять, нет ли новой версии (обновит только после вашего подтверждения)." : "Проверка обновлений выключена.";
    }

    internal bool CheckUpdatesOn => _store.Data.CheckUpdates;
}
