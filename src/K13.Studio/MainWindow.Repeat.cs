using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v4.8: macro repeat mode as a segmented pill control (1× · N× with stepper · while held · toggle).
/// All four modes are stored in the keypad's macro table and run by the keypad itself (no PC needed).</summary>
public partial class MainWindow
{
    private enum RepeatChoice { Once, Times, Hold, Toggle }

    private static RepeatChoice ChoiceOf(MacroSlot m) => m.RepeatMode switch
    {
        MacroRepeatMode.WhileHeld => RepeatChoice.Hold,
        MacroRepeatMode.Toggle => RepeatChoice.Toggle,
        _ => m.Repeat > 1 ? RepeatChoice.Times : RepeatChoice.Once,
    };

    private static string RepeatExplain(RepeatChoice c, int n) => c switch
    {
        RepeatChoice.Once => "Одно нажатие — макрос выполняется один раз.",
        RepeatChoice.Times => $"Одно нажатие — макрос выполняется {n} {TimesWord(n)} подряд.",
        RepeatChoice.Hold => "Макрос повторяется по кругу, пока кнопка зажата. Отпустили — остановился.",
        _ => "Первое нажатие запускает макрос по кругу, второе нажатие той же кнопки — останавливает.",
    };

    private static string TimesWord(int n) => RepeatCount.Word(n);

    private FrameworkElement RepeatSelector(int sel, MacroSlot slot)
    {
        var choice = ChoiceOf(slot);
        var root = new StackPanel();

        var wrap = new WrapPanel();
        var group = new Border
        {
            Background = R("Panel2Brush"), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, Child = wrap,
        };
        root.Children.Add(group);

        var explain = T(RepeatExplain(choice, slot.Repeat), 12.5, muted: true).With(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(4, 8, 0, 0); });
        root.Children.Add(explain);
        var onDevice = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 0, 0) };
        onDevice.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = Icons, FontSize = 11, Foreground = R("OkBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        onDevice.Children.Add(T("Работает в самой клавиатуре — программа на ПК для этого не нужна.", 11.5, muted: true));
        if (choice is RepeatChoice.Hold or RepeatChoice.Toggle) root.Children.Add(onDevice);

        void Set(RepeatChoice c)
        {
            if (c == ChoiceOf(slot)) return;
            switch (c)
            {
                case RepeatChoice.Once: slot.RepeatMode = MacroRepeatMode.Count; slot.Repeat = 1; break;
                case RepeatChoice.Times: slot.RepeatMode = MacroRepeatMode.Count; slot.Repeat = Math.Max(2, _lastTimes.GetValueOrDefault(sel, 3)); break;
                case RepeatChoice.Hold: slot.RepeatMode = MacroRepeatMode.WhileHeld; slot.Repeat = 1; break;
                case RepeatChoice.Toggle: slot.RepeatMode = MacroRepeatMode.Toggle; slot.Repeat = 1; break;
            }
            Changed(false);
            RefreshMacros();
        }

        var labels = new Dictionary<RepeatChoice, (TextBlock tb, string full)>();
        Border Segment(RepeatChoice c, string? badge, string? glyph, string label, string tip, FrameworkElement? extra = null)
        {
            bool on = choice == c;
            var fg = on ? R("OnAccentBrush") : R("TextBrush");
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (badge != null)
                sp.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                    Background = on ? new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)) : R("AccentSoftBrush"),
                    Child = new TextBlock { Text = badge, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = on ? fg : R("AccentBrush") },
                });
            else
                sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = on ? fg : R("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 8, 0) });
            var lt = new TextBlock { Text = label, FontSize = 13, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            labels[c] = (lt, label);
            sp.Children.Add(lt);
            if (extra != null) sp.Children.Add(extra);
            var seg = new Border
            {
                Child = sp, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 7, 13, 7), Margin = new Thickness(0),
                Background = on ? R("AccentBrush") : Brushes.Transparent, Cursor = on ? Cursors.Arrow : Cursors.Hand, MinHeight = 36,
                ToolTip = new ToolTip { Content = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 } },
                SnapsToDevicePixels = true,
            };
            ToolTipService.SetInitialShowDelay(seg, 350);
            if (!on)
            {
                seg.MouseEnter += (_, _) => seg.Background = R("HoverBrush");
                seg.MouseLeave += (_, _) => seg.Background = Brushes.Transparent;
                seg.MouseLeftButtonUp += (_, e) => { e.Handled = true; Set(c); };
            }
            AutomationPropertiesHelper(seg, label);
            return seg;
        }

        // N× stepper (only inside the selected segment)
        FrameworkElement? stepper = null;
        if (choice == RepeatChoice.Times)
        {
            var st = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, -3, -5, -3), VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox
            {
                Text = slot.Repeat.ToString(), Width = 46, Height = 26, Padding = new Thickness(0), TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Background = R("PanelBrush"), Foreground = R("TextBrush"), ToolTip = "Сколько раз подряд (2…65535). Сохраняется сразу; ↑/↓ — на 1.",
            };
            Button Step(string glyph, int d) 
            {
                var b = new Button
                {
                    Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(d < 0 ? 0 : 4, 0, d < 0 ? 4 : 0, 0), Focusable = false,
                    Style = (Style)FindResource("GhostButton"), Foreground = R("OnAccentBrush"), ToolTip = d < 0 ? "Меньше" : "Больше",
                    Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 11, Foreground = R("OnAccentBrush") },
                };
                b.Click += (_, e) =>
                {
                    e.Handled = true;
                    int n = RepeatCount.Clamp(slot.Repeat + d);
                    if (n == slot.Repeat) return;
                    slot.Repeat = n; _lastTimes[sel] = n; box.Text = n.ToString();
                    explain.Text = RepeatExplain(RepeatChoice.Times, n);
                    Changed(false);
                };
                return b;
            }
            // commit on every keystroke (clamped to 2…65535) – no focus loss / Enter needed
            box.TextChanged += (_, _) =>
            {
                if (RepeatCount.TryParse(box.Text, out var r) && r != slot.Repeat)
                { slot.Repeat = r; _lastTimes[sel] = r; explain.Text = RepeatExplain(RepeatChoice.Times, r); Changed(false); }
            };
            box.LostKeyboardFocus += (_, _) => box.Text = slot.Repeat.ToString();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; box.Text = slot.Repeat.ToString(); Keyboard.ClearFocus(); }
                else if (e.Key is Key.Up or Key.Down)
                {
                    e.Handled = true;
                    slot.Repeat = RepeatCount.Clamp(slot.Repeat + (e.Key == Key.Up ? 1 : -1)); _lastTimes[sel] = slot.Repeat;
                    box.Text = slot.Repeat.ToString(); box.CaretIndex = box.Text.Length;
                }
            };
            DataObject.AddPastingHandler(box, (_, e) => { if (!(e.DataObject.GetData(typeof(string)) is string t && t.Trim().All(char.IsDigit))) e.CancelCommand(); });
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            box.PreviewMouseLeftButtonUp += (_, e) => e.Handled = true;
            st.Children.Add(Step("\uE738", -1));
            st.Children.Add(new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = box, Background = R("PanelBrush") });
            st.Children.Add(Step("\uE710", +1));
            stepper = st;
        }

        wrap.Children.Add(Segment(RepeatChoice.Once, "1×", null, "Один раз", "Один раз: каждое нажатие кнопки выполняет макрос один раз."));
        wrap.Children.Add(Segment(RepeatChoice.Times, "N×", null, choice == RepeatChoice.Times ? "Раз" : "N раз", "Несколько раз: одно нажатие выполняет макрос заданное число раз подряд (2…65535).", stepper));
        wrap.Children.Add(Segment(RepeatChoice.Hold, null, "\uE7C9", "Пока зажата", "Пока зажата: макрос крутится по кругу, пока вы держите кнопку, и останавливается, когда её отпускаете."));
        wrap.Children.Add(Segment(RepeatChoice.Toggle, null, "\uE8EE", "До повторного нажатия",
            "До повторного нажатия: первое нажатие запускает макрос по кругу, второе нажатие той же кнопки останавливает. Режим встроен в клавиатуру (как в родной программе) и работает без ПК."));
        // narrow editor: tidy 2×2 grid instead of a ragged wrap
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        root.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 600;
            // short labels in the 2×2 grid so nothing is clipped
            if (labels.TryGetValue(RepeatChoice.Toggle, out var tg)) tg.tb.Text = narrow ? "До повторного" : tg.full;
            if (labels.TryGetValue(RepeatChoice.Times, out var tm)) tm.tb.Visibility = narrow && choice == RepeatChoice.Times ? Visibility.Collapsed : Visibility.Visible;
            if (labels.TryGetValue(RepeatChoice.Hold, out var hd)) hd.tb.Text = hd.full;
            if (narrow && group.Child == wrap)
            {
                var items = wrap.Children.Cast<UIElement>().ToList(); wrap.Children.Clear();
                foreach (var it in items) grid.Children.Add(it);
                group.Child = grid; group.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            else if (!narrow && group.Child == grid)
            {
                var items = grid.Children.Cast<UIElement>().ToList(); grid.Children.Clear();
                foreach (var it in items) wrap.Children.Add(it);
                group.Child = wrap; group.HorizontalAlignment = HorizontalAlignment.Left;
            }
        };
        return root;
    }

    private readonly Dictionary<int, int> _lastTimes = new();

    private static void AutomationPropertiesHelper(FrameworkElement e, string name) => System.Windows.Automation.AutomationProperties.SetName(e, name);
}
