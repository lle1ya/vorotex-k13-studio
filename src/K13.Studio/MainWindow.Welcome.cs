using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Vorotex.Studio;

/// <summary>First-run 3-slide welcome overlay (reopen with «?»).</summary>
public partial class MainWindow
{
    private int _welcomeSlide;

    private static readonly (string Glyph, string Title, string Text)[] Slides =
    {
        ("", "Добро пожаловать!",
         "Здесь вы настраиваете свою VOROTEX K13: что делает каждая кнопка, джойстик и колесо, как светится подсветка и какие макросы она запускает."),
        ("\uE765", "Как назначить кнопку",
         "Откройте «Кнопки» и нажмите на нужную кнопку прямо на картинке. Справа появится вопрос «Что делает кнопка?» — выберите вариант: клавиша, сочетание, мультимедиа, программа или макрос — и нажмите «Готово»."),
        ("\uE706", "Подсветка и макросы",
         "«Подсветка» — три шага: эффект, цвет, яркость и скорость.\n«Макросы» — нажмите «Запись», нажмите клавиши, затем «Стоп».\n\nПока включён безопасный режим: программа только читает клавиатуру. Запись в неё включим после первой совместной проверки."),
    };

    private void HelpBtn_Click(object sender, RoutedEventArgs e) => ShowWelcome(true);

    internal void ShowWelcome(bool show, int slide = 0)
    {
        Welcome.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            if (!_store.Data.WelcomeShown) { _store.Data.WelcomeShown = true; SaveStore(); }
            return;
        }
        _welcomeSlide = slide;
        BuildWelcome();
    }

    private void BuildWelcome()
    {
        WelcomeHost.Children.Clear();
        var (glyph, title, text) = Slides[_welcomeSlide];
        if (_welcomeSlide == 0)
        {
            var img = new DeviceView(interactive: false) { Height = 180, Margin = new Thickness(0, 0, 0, 18) };
            img.Rebuild(Cfg, _profile.Host, -1, false);
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            WelcomeHost.Children.Add(img);
        }
        else
        {
            WelcomeHost.Children.Add(new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(20), Background = R("AccentSoftBrush"), Margin = new Thickness(0, 6, 0, 18),
                Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 32, Foreground = R("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        }
        WelcomeHost.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) });
        WelcomeHost.Children.Add(new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Foreground = R("MutedBrush"), LineHeight = 21, MinHeight = 64 });

        var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 18) };
        for (int i = 0; i < Slides.Length; i++)
            dots.Children.Add(new Border { Width = i == _welcomeSlide ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(3, 0, 3, 0), Background = i == _welcomeSlide ? R("AccentBrush") : R("TrackBrush") });
        WelcomeHost.Children.Add(dots);

        var row = new DockPanel();
        bool last = _welcomeSlide == Slides.Length - 1;
        var next = new Button { Content = last ? "Начать" : "Далее", Style = (Style)FindResource("AccentButton"), MinWidth = 120 };
        next.Click += (_, _) => { if (last) ShowWelcome(false); else { _welcomeSlide++; BuildWelcome(); } };
        DockPanel.SetDock(next, Dock.Right);
        row.Children.Add(next);
        if (_welcomeSlide > 0)
        {
            var back = new Button { Content = "Назад", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(0, 0, 8, 0) };
            back.Click += (_, _) => { _welcomeSlide--; BuildWelcome(); };
            DockPanel.SetDock(back, Dock.Right);
            row.Children.Add(back);
        }
        var skip = new Button { Content = "Пропустить", Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Visibility = last ? Visibility.Hidden : Visibility.Visible };
        skip.Click += (_, _) => ShowWelcome(false);
        row.Children.Add(skip);
        WelcomeHost.Children.Add(row);
    }
}
