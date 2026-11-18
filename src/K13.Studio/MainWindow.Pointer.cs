using System.Windows;
using System.Windows.Controls;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>1.3.0: tile «Прокрутка и курсор» — horizontal / vertical scroll and cursor movement, executed on the PC.</summary>
public partial class MainWindow
{
    private PointerSpec? _ptPending;

    /// <summary>The pointer action bound to a keypad code (F13–F24 trigger), or null.</summary>
    internal PointerSpec? PointerOf(byte code) =>
        code is >= 0x68 and <= 0x73 ? PointerSpec.From(_profile.Host.Keys.FirstOrDefault(k => k.TriggerVk == 0x7C + (code - 0x68))?.Tap) : null;

    private static PointerDir DirForColumn(int col) => col switch { 12 => PointerDir.Left, 13 => PointerDir.Right, 14 => PointerDir.Down, 15 => PointerDir.Up, _ => PointerDir.Right };

    internal const string PointerLimits =
        "Выполняет программа на ПК (пока она запущена, можно в трее). Кнопка, у которой действие на ПК, посылает короткое нажатие — " +
        "программа не видит, сколько её держат. Поэтому: «За нажатие» — каждое нажатие (наклон стика) прокручивает на заданное число щелчков; " +
        "«Старт/стоп» — нажатие запускает прокрутку или движение, повторное нажатие — останавливает (другое направление — меняет направление, " +
        "любая другая кнопка с действием — останавливает; сама остановится через 20 с).";

    private Action PointerEditor(byte code)
    {
        var cur = PointerOf(code);
        var s = _ptPending ?? cur ?? new PointerSpec(PointerKind.Scroll, DirForColumn(_selCol), PointerMode.Step, PointerSpec.DefaultAmount(PointerKind.Scroll));
        _ptPending = s;
        var host = new StackPanel();
        EditorHost.Children.Add(host);
        void Paint()
        {
            host.Children.Clear();
            var p = _ptPending!;
            host.Children.Add(T("Что делать:", 13, muted: true, bottom: 6));
            var kinds = new WrapPanel();
            foreach (var k in new[] { PointerKind.Scroll, PointerKind.Move })
                foreach (var d in new[] { PointerDir.Left, PointerDir.Right, PointerDir.Up, PointerDir.Down })
                {
                    var spec = p with { Kind = k, Dir = d, Amount = k == p.Kind ? p.Amount : PointerSpec.DefaultAmount(k) };
                    kinds.Children.Add(Chip("", (k == PointerKind.Scroll ? "Прокрутка " : "Курсор ") + PointerSpec.Arrow(d), p.Kind == k && p.Dir == d,
                        () => { _ptPending = spec; Paint(); }, k == PointerKind.Scroll ? (d is PointerDir.Left or PointerDir.Right ? "по горизонтали" : "колесо") : "мышь"));
                }
            host.Children.Add(kinds);
            host.Children.Add(T("Как работает нажатие:", 13, muted: true).With(t => t.Margin = new Thickness(0, 12, 0, 6)));
            var modes = new WrapPanel();
            modes.Children.Add(Chip("", "За нажатие", p.Mode == PointerMode.Step, () => { _ptPending = p with { Mode = PointerMode.Step }; Paint(); },
                p.Kind == PointerKind.Scroll ? "N щелчков на нажатие" : "шаг, частые нажатия ускоряют"));
            modes.Children.Add(Chip("", "Старт/стоп", p.Mode == PointerMode.Toggle, () => { _ptPending = p with { Mode = PointerMode.Toggle }; Paint(); },
                "нажали — идёт, ещё раз — стоп"));
            host.Children.Add(modes);
            host.Children.Add(T(p.Mode == PointerMode.Step ? (p.Kind == PointerKind.Scroll ? "Щелчков колеса за нажатие:" : "Шаг курсора:") : "Скорость:", 13, muted: true)
                .With(t => t.Margin = new Thickness(0, 12, 0, 6)));
            var amounts = new WrapPanel();
            foreach (var n in new[] { 1, 2, 3, 4, 5, 6, 8, 10 })
                amounts.Children.Add(Chip("", n.ToString(), p.Amount == n, () => { _ptPending = p with { Amount = n }; Paint(); }));
            host.Children.Add(amounts);
            host.Children.Add(T("Сейчас: " + p.Title + " — " + p.Details + ".", 12.5).With(t => { t.Margin = new Thickness(0, 10, 0, 0); t.TextWrapping = TextWrapping.Wrap; }));
            if (StickTilts.IsTilt(_selCol) || _selCol == DeviceView.JoystickKnob)
            {
                var all = new Button { Content = (p.Kind == PointerKind.Scroll ? "Прокрутка" : "Курсор") + " на все 4 наклона стика", Style = (Style)FindResource("GhostButton"),
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
                all.Click += (_, _) =>
                {
                    var e = AssignPointerToStick(p.Kind, p.Mode, p.Amount);
                    StatusText.Text = e ?? $"Стик: {(p.Kind == PointerKind.Scroll ? "прокрутка" : "курсор")} во все 4 стороны — нажмите «Записать».";
                    if (e == null) { _tile = null; BuildEditor(); }
                };
                host.Children.Add(all);
            }
            host.Children.Add(T(PointerLimits, 12, muted: true).With(t => { t.Margin = new Thickness(0, 12, 0, 0); t.TextWrapping = TextWrapping.Wrap; }));
            if (p.Kind == PointerKind.Move)
                host.Children.Add(T("Нужен курсор, который едет, только пока кнопка зажата? Это умеет одна кнопка: «Макрос» с движением мыши и повтором «Пока зажата» " +
                                    "(как «Удерживать до отпускания» — код удержания один на клавиатуру).", 12, muted: true)
                    .With(t => { t.Margin = new Thickness(0, 8, 0, 0); t.TextWrapping = TextWrapping.Wrap; }));
            if (p.Kind == PointerKind.Scroll && p.Dir is PointerDir.Left or PointerDir.Right)
                host.Children.Add(T("Excel: выделенная ячейка не меняется — двигается только видимая часть листа, как колесом мыши с наклоном.", 12, muted: true)
                    .With(t => { t.Margin = new Thickness(0, 8, 0, 0); t.TextWrapping = TextWrapping.Wrap; }));
        }
        Paint();
        return () => { if (_ptPending is { } p) _lastError = ApplyPointer(p); _ptPending = null; };
    }

    /// <summary>Assigns the pointer action to the selected control (a PC action: F13–F24 code + Scroll Lock + F chord in the keypad).</summary>
    internal string? ApplyPointer(PointerSpec p) => ApplyHost(p.ToAction(), new HostAction(), 400);

    /// <summary>Scroll or cursor on all four stick tilts (← → ↓ ↑ = columns 12–15), each in its own direction.</summary>
    internal string? AssignPointerToStick(PointerKind kind, PointerMode mode, int amount)
    {
        int keep = _selCol;
        try
        {
            foreach (var col in StickTilts.Columns)
            {
                _selCol = col;
                var e = ApplyPointer(new PointerSpec(kind, DirForColumn(col), mode, amount));
                if (e != null) return e;
            }
            return null;
        }
        finally { _selCol = keep; }
    }
}
