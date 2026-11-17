using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.10: «Проверять обновления» — asks GitHub once a day whether a newer release exists. Notify only: opens the release
/// page on request, never downloads or installs anything. Off switch: ⋯ → «Проверять обновления».</summary>
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
            if (_store.Data.CheckUpdates && UpdateCheck.Due(_store.Data.LastUpdateCheck, DateTime.UtcNow)) await CheckUpdatesAsync(manual: false);
        };
        t.Start();
    }

    internal async Task CheckUpdatesAsync(bool manual)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            string json;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("VorotexK13Studio/" + CurrentVersion);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                json = await http.GetStringAsync(UpdateCheck.ApiUrl);
            }
            _store.Data.LastUpdateCheck = DateTime.UtcNow; SaveStore();
            var r = UpdateCheck.Parse(json);
            if (r == null) { Log("Обновления: ответ GitHub не разобран."); if (manual) MessageBox.Show(this, "Не удалось понять ответ GitHub. Откройте страницу выпусков вручную:\n" + UpdateCheck.ReleasesPage, Title); return; }
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
            if (manual) MessageBox.Show(this, "Не удалось проверить обновления — нет связи с GitHub. Попробуйте позже или откройте страницу выпусков вручную:\n" + UpdateCheck.ReleasesPage, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _updateBusy = false; }
    }

    private void OfferUpdate(UpdateCheck.Release r)
    {
        var res = MessageBox.Show(this,
            $"Вышла новая версия VOROTEX K13 Studio: {r.Tag} (у вас {UpdateCheck.Display(CurrentVersion)}).\n\n" +
            "Открыть страницу выпуска? Скачивать и устанавливать вы будете сами — программа ничего не загружает.\n" +
            "Проверку можно выключить: ⋯ → «Проверять обновления».",
            "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (res == MessageBoxResult.Yes) OpenReleasePage(r.Url);
        else { _store.Data.SkippedUpdate = r.Tag; SaveStore(); }
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
        StatusText.Text = on ? "Программа будет раз в день проверять, нет ли новой версии (только сообщит)." : "Проверка обновлений выключена.";
    }

    internal bool CheckUpdatesOn => _store.Data.CheckUpdates;
}
