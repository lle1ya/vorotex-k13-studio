using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>Advanced dialog: host trigger keys (tap/hold) and chords (joystick diagonals).</summary>
internal static class HostActionsWindow
{
    private sealed record KeyRow(string Trigger, string Tap, string Hold, int HoldMs, string Suppress, HostKeyBinding Src);
    private sealed record ChordRow(string Title, string Keys, string Action, HostChordBinding Src);

    public static void Show(Window owner, HostSettings host, IEnumerable<string> profiles, Action setupDiagonals, Func<ushort> freeVk)
    {
        var names = profiles.ToList();
        var win = Ui.MakeWindow(owner, "Действия на ПК", 860, 620);
        win.MinWidth = 640; win.MinHeight = 460;
        var root = new DockPanel { Margin = new Thickness(24) };
        var head = new StackPanel();
        head.Children.Add(new TextBlock { Text = "Действия на ПК", FontSize = 20, FontWeight = FontWeights.SemiBold });
        head.Children.Add(Ui.Label("Клавиатура посылает «редкую» клавишу (F13–F24), а программа превращает её в действие. Отличает короткое нажатие от удержания и распознаёт диагонали джойстика (два направления сразу).", true, 4));
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var keys = new DataGrid { IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(0, 8, 0, 0) };
        keys.Columns.Add(new DataGridTextColumn { Header = "Триггер", Binding = new System.Windows.Data.Binding("Trigger"), Width = 90 });
        keys.Columns.Add(new DataGridTextColumn { Header = "Нажатие", Binding = new System.Windows.Data.Binding("Tap"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        keys.Columns.Add(new DataGridTextColumn { Header = "Удержание", Binding = new System.Windows.Data.Binding("Hold"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        keys.Columns.Add(new DataGridTextColumn { Header = "мс", Binding = new System.Windows.Data.Binding("HoldMs"), Width = 60 });
        var chords = new DataGrid { IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single, Margin = new Thickness(0, 8, 0, 0) };
        chords.Columns.Add(new DataGridTextColumn { Header = "Название", Binding = new System.Windows.Data.Binding("Title"), Width = 160 });
        chords.Columns.Add(new DataGridTextColumn { Header = "Клавиши", Binding = new System.Windows.Data.Binding("Keys"), Width = 120 });
        chords.Columns.Add(new DataGridTextColumn { Header = "Действие", Binding = new System.Windows.Data.Binding("Action"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        void Refresh()
        {
            keys.ItemsSource = host.Keys.Select(k => new KeyRow(KeyCombo.VkName(k.TriggerVk), k.Tap.ToString(), k.Hold.ToString(), k.HoldMs, k.Suppress ? "да" : "нет", k)).ToList();
            chords.ItemsSource = host.Chords.Select(c => new ChordRow(c.Title, $"{KeyCombo.VkName(c.VkA)} + {KeyCombo.VkName(c.VkB)}", c.Action.ToString(), c)).ToList();
        }
        Button Btn(string text, Action a, bool ghost = false)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0) };
            if (ghost) b.Style = (Style)Application.Current.FindResource("GhostButton");
            b.Click += (_, _) => { a(); Refresh(); };
            return b;
        }
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        var bar1 = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        bar1.Children.Add(new TextBlock { Text = "Триггеры", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        bar1.Children.Add(Btn("+ Добавить", () => { var b = new HostKeyBinding { TriggerVk = freeVk() }; if (HostBindingEditor.Edit(win, b, names)) host.Keys.Add(b); }));
        bar1.Children.Add(Btn("Изменить…", () => { if (keys.SelectedItem is KeyRow r) HostBindingEditor.Edit(win, r.Src, names); }, true));
        bar1.Children.Add(Btn("Удалить", () => { if (keys.SelectedItem is KeyRow r) host.Keys.Remove(r.Src); }, true));
        var bar2 = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        bar2.Children.Add(new TextBlock { Text = "Диагонали (аккорды)", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
        bar2.Children.Add(Btn("Настроить диагонали джойстика", setupDiagonals));
        bar2.Children.Add(Btn("+ Аккорд", () => { var c = new HostChordBinding { Title = "Новый аккорд", VkA = 0x7C, VkB = 0x7D }; if (HostBindingEditor.EditChord(win, c, names)) host.Chords.Add(c); }, true));
        bar2.Children.Add(Btn("Изменить…", () => { if (chords.SelectedItem is ChordRow r) HostBindingEditor.EditChord(win, r.Src, names); }, true));
        bar2.Children.Add(Btn("Удалить", () => { if (chords.SelectedItem is ChordRow r) host.Chords.Remove(r.Src); }, true));
        keys.MouseDoubleClick += (_, _) => { if (keys.SelectedItem is KeyRow r && HostBindingEditor.Edit(win, r.Src, names)) Refresh(); };
        chords.MouseDoubleClick += (_, _) => { if (chords.SelectedItem is ChordRow r && HostBindingEditor.EditChord(win, r.Src, names)) Refresh(); };
        Grid.SetRow(bar1, 0); Grid.SetRow(keys, 1); Grid.SetRow(bar2, 2); Grid.SetRow(chords, 3);
        grid.Children.Add(bar1); grid.Children.Add(keys); grid.Children.Add(bar2); grid.Children.Add(chords);
        var close = new Button { Content = "Готово", Style = (Style)Application.Current.FindResource("AccentButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), IsCancel = true };
        close.Click += (_, _) => win.Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(grid);
        win.Content = root;
        Refresh();
        win.ShowDialog();
    }
}

/// <summary>Device info / memory hex / log, and generic text viewer.</summary>
internal static class DeviceWindow
{
    public static void Show(Window owner, string info, byte[]? image, string log)
    {
        var hex = new System.Text.StringBuilder();
        if (image != null)
            for (int i = 0; i < image.Length; i += 16)
                hex.AppendLine($"{i:X4}: " + string.Join(" ", image.Skip(i).Take(16).Select(b => b.ToString("X2"))));
        var win = Ui.MakeWindow(owner, "Устройство и журнал", 980, 640);
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var infoText = new TextBlock { Text = info, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetColumnSpan(infoText, 3);
        root.Children.Add(infoText);
        TextBox Box(string text) => new() { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
        var h = Box(image == null ? "Память ещё не прочитана." : hex.ToString()); Grid.SetRow(h, 1);
        var l = Box(log); Grid.SetRow(l, 1); Grid.SetColumn(l, 2); l.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(h); root.Children.Add(l);
        win.Content = root;
        win.ShowDialog();
    }

    public static void ShowText(Window owner, string title, string text)
    {
        var win = Ui.MakeWindow(owner, title, 900, 640);
        win.Content = new TextBox
        {
            Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Margin = new Thickness(16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        win.ShowDialog();
    }
}
