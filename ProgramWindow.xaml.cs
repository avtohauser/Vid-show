using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VidShow
{
    // План мероприятия: все элементы очереди по порядку, с длительностями, паузами, повторами и переходами.
    // Блоки можно перетаскивать, двойной клик — включить элемент.
    public partial class ProgramWindow : Window
    {
        private readonly ObservableCollection<PlayItem> _items;
        private readonly Action<PlayItem> _select, _play, _edit;
        private readonly Action _changed;
        private readonly Func<PlayItem> _current;
        private readonly Func<double> _progress;
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly Dictionary<PlayItem, Border> _blocks = new Dictionary<PlayItem, Border>();
        private readonly Dictionary<PlayItem, Rectangle> _bars = new Dictionary<PlayItem, Rectangle>();
        private PlayItem _selected;
        private Point _dragStart;

        public ProgramWindow(ObservableCollection<PlayItem> items, Action<PlayItem> select, Action<PlayItem> play,
                             Action<PlayItem> edit, Action changed, Func<PlayItem> current, Func<double> progress)
        {
            InitializeComponent();
            _items = items; _select = select; _play = play; _edit = edit;
            _changed = changed; _current = current; _progress = progress;
            _timer.Tick += (s, e) => UpdateProgress();
            _timer.Start();
            Rebuild();
        }

        private void Window_SourceInitialized(object sender, EventArgs e) =>
            Native.DarkTitleBar(new WindowInteropHelper(this).Handle);

        private void Window_Closed(object sender, EventArgs e) => _timer.Stop();

        private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IsLoaded) Rebuild();
        }

        private void Edit_Click(object sender, RoutedEventArgs e)
        {
            if (_selected != null) { _edit(_selected); Rebuild(); }
        }

        private static string Fmt(double s) => PlayItem.FmtTime(s);

        public void Rebuild()
        {
            Strip.Children.Clear();
            _blocks.Clear();
            _bars.Clear();
            double pps = Zoom.Value;
            double t = 0;
            int waits = 0, unknown = 0, still = 0;
            var cur = _current();

            if (_items.Count == 0)
            {
                Strip.Children.Add(new TextBlock
                {
                    Text = "Программа пуста. Добавьте видео и картинки в очередь — здесь появится план.",
                    Foreground = (Brush)FindResource("MutedBrush"), FontSize = 14, Margin = new Thickness(8)
                });
                Summary.Text = "";
                return;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                double dur;       // сколько займёт на плане
                string durText;
                bool waits_ = it.Hold || (!it.IsImage && it.Loops == 0) || it.EndBlack;
                if (it.IsImage)
                {
                    double secs = it.Secs > 0 ? it.Secs : 10;
                    dur = it.Hold ? 0 : secs;
                    durText = it.Hold ? "ждёт оператора" : Fmt(secs);
                    if (!it.Hold) still++;
                }
                else if (it.OnePass > 0)
                {
                    int times = it.Loops == 0 ? 1 : Math.Max(1, it.Loops);
                    dur = it.OnePass * times;
                    durText = Fmt(it.OnePass) + (it.Loops == 0 ? "  🔁∞" : it.Loops > 1 ? "  ×" + it.Loops : "");
                }
                else { dur = 0; durText = it.Duration < 0 ? "?" : "…"; unknown++; }
                if (waits_) waits++;

                double w = it.IsImage
                    ? (it.Hold ? 130 : Math.Max(120, Math.Min(400, dur * pps)))
                    : Math.Max(150, Math.Min(900, dur * pps));

                var block = MakeBlock(it, i + 1, w, t, durText, it == cur);
                Strip.Children.Add(block);
                _blocks[it] = block;
                t += dur;

                if (i < _items.Count - 1) Strip.Children.Add(MakeConnector(it, _items[i + 1], waits_));
            }

            Summary.Text = "Расчётная длительность: " + Fmt(t) +
                           (waits > 0 ? "  ·  пауз «ждать оператора»: " + waits + " (в расчёт не входят)" : "") +
                           (unknown > 0 ? "  ·  длительность ещё определяется у файлов: " + unknown : "") +
                           "  ·  перетаскивайте блоки, чтобы поменять порядок; двойной клик — включить";
            UpdateProgress();
        }

        private Border MakeBlock(PlayItem it, int num, double w, double startAt, string durText, bool playing)
        {
            Color bg = it.IsBlack ? Color.FromRgb(0x14, 0x16, 0x1B)
                     : it.IsImage ? Color.FromRgb(0x3A, 0x2A, 0x66)
                     : Color.FromRgb(0x1F, 0x3A, 0x68);

            var grid = new Grid { Margin = new Thickness(10, 8, 10, 8) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new TextBlock
            {
                Text = num + ".  " + it.Name, FontWeight = FontWeights.SemiBold, FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
            };
            grid.Children.Add(title);

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            mid.Children.Add(new TextBlock { Text = it.IsBlack ? "чёрный экран" : it.IsImage ? "картинка" : "видео", FontSize = 11, Foreground = (Brush)FindResource("MutedBrush") });
            mid.Children.Add(new TextBlock { Text = durText, FontSize = 18, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 4, 0, 4) });
            mid.Children.Add(new TextBlock { Text = "старт ≈ " + Fmt(startAt), FontSize = 11, Foreground = (Brush)FindResource("MutedBrush") });
            Grid.SetRow(mid, 1);
            grid.Children.Add(mid);

            var icons = new TextBlock
            {
                Text = it.Badge, FontSize = 14, Margin = new Thickness(0, 4, 0, 6), Foreground = (Brush)FindResource("AccentLightBrush")
            };
            Grid.SetRow(icons, 2);
            grid.Children.Add(icons);

            var bar = new Rectangle
            {
                Height = 4, Fill = (Brush)FindResource("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom, Width = 0, RadiusX = 2, RadiusY = 2
            };
            _bars[it] = bar;

            var root = new Grid();
            root.Children.Add(grid);
            root.Children.Add(bar);

            var border = new Border
            {
                Width = w, Height = 170, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(bg),
                BorderThickness = new Thickness(2), Child = root, Cursor = Cursors.Hand, AllowDrop = true,
                ClipToBounds = true, Tag = it, ToolTip = Describe(it)
            };
            Paint(border, it, playing);
            border.MouseLeftButtonDown += Block_Down;
            border.MouseMove += Block_Move;
            border.DragOver += (s, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
            border.Drop += Block_Drop;
            return border;
        }

        private void Paint(Border b, PlayItem it, bool playing)
        {
            Brush accent = (Brush)FindResource("AccentBrush");
            b.BorderBrush = playing ? accent : it == _selected ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x33, 0x38, 0x48));
        }

        private UIElement MakeConnector(PlayItem from, PlayItem to, bool wait)
        {
            string text = wait ? "⏸" : "→";
            string tip = wait ? "Здесь программа ждёт оператора" : "Дальше сама";
            double fade = to.Fade >= 0 ? to.Fade : -1;
            if (to.ViaBlack) tip += "\nПереход через чёрное";
            if (fade >= 0) tip += "\nПереход " + fade.ToString("0.#") + " с";
            if (to.ViaBlack) text += "◐";
            return new TextBlock
            {
                Text = text, FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
                Foreground = wait ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4D)) : (Brush)FindResource("MutedBrush"),
                ToolTip = tip
            };
        }

        private static string Describe(PlayItem it)
        {
            var sb = new StringBuilder(it.Name);
            if (!it.IsBlack) sb.Append('\n').Append(it.Path);
            if (!it.IsImage && it.Duration > 0) sb.Append("\nДлительность файла: ").Append(Fmt(it.Duration));
            if (!it.IsImage && (it.In > 0 || it.Out > 0))
                sb.Append("\nОбрезка: с ").Append(Fmt(it.In)).Append(" до ").Append(it.Out > 0 ? Fmt(it.Out) : "конца");
            if (!it.IsImage && it.Loops != 1) sb.Append("\nПовтор: ").Append(it.Loops == 0 ? "по кругу, пока не нажмут «дальше»" : it.Loops + " раз");
            if (it.Hold) sb.Append(it.IsImage ? "\nДержится, пока оператор не включит следующее" : "\nПосле конца ждёт оператора");
            if (it.EndBlack) sb.Append("\nВ конце затемняется и ждёт");
            if (it.Fade >= 0) sb.Append("\nПереход в него: ").Append(it.Fade.ToString("0.#")).Append(" с");
            if (it.ViaBlack) sb.Append("\nПереход через чёрное");
            if (!string.IsNullOrEmpty(it.Sfx)) sb.Append("\nФанфара: ").Append(System.IO.Path.GetFileName(it.Sfx));
            if (it.MusicAct == 1) sb.Append("\nВключает фоновую музыку");
            if (it.MusicAct == 2) sb.Append("\nПлавно выключает музыку");
            return sb.ToString();
        }

        private void UpdateProgress()
        {
            var cur = _current();
            double frac = Math.Max(0, Math.Min(1, _progress()));
            foreach (var kv in _bars)
            {
                var block = _blocks.ContainsKey(kv.Key) ? _blocks[kv.Key] : null;
                bool playing = kv.Key == cur;
                kv.Value.Width = playing && block != null ? frac * block.Width : 0;
                if (block != null) Paint(block, kv.Key, playing);
            }
        }

        // ---------- мышь ----------

        private void Block_Down(object sender, MouseButtonEventArgs e)
        {
            var b = (Border)sender;
            var it = (PlayItem)b.Tag;
            _dragStart = e.GetPosition(null);
            _selected = it;
            _select(it);
            foreach (var kv in _blocks) Paint(kv.Value, kv.Key, kv.Key == _current());
            if (e.ClickCount == 2) _play(it);
        }

        private void Block_Move(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(null) - _dragStart;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var b = (Border)sender;
            DragDrop.DoDragDrop(b, new DataObject("vidshow-cue", b.Tag), DragDropEffects.Move);
        }

        private void Block_Drop(object sender, DragEventArgs e)
        {
            var src = e.Data.GetData("vidshow-cue") as PlayItem;
            var dst = (PlayItem)((Border)sender).Tag;
            if (src == null || src == dst) return;
            int from = _items.IndexOf(src), to = _items.IndexOf(dst);
            if (from < 0 || to < 0) return;
            _items.Move(from, to);
            _changed();
            Rebuild();
            e.Handled = true;
        }
    }
}
