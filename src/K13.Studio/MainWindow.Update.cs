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
