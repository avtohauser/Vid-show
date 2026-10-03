using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VidShow
{
    public class PlayItem : INotifyPropertyChanged
    {
        private bool _playing;
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public bool IsPlaying
        {
            get => _playing;
            set { _playing = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal static class Settings
    {
        public static int ScreenIdx;
        public static double Volume = 0.8;
        public static bool Loop, Mute;
        public static bool Preview = true;

        private static string FilePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VidShow", "settings.txt");

        public static void Load()
        {
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split(new[] { '=' }, 2);
                    if (p.Length != 2) continue;
                    var v = p[1].Trim();
                    switch (p[0].Trim())
                    {
                        case "screen": int.TryParse(v, out ScreenIdx); break;
                        case "volume": double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out Volume); break;
                        case "loop": Loop = v == "1"; break;
                        case "mute": Mute = v == "1"; break;
                        case "preview": Preview = v != "0"; break;
                    }
                }
            }
            catch { /* первый запуск или файл недоступен — берём значения по умолчанию */ }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "screen=" + ScreenIdx,
                    "volume=" + Volume.ToString("0.00", CultureInfo.InvariantCulture),
                    "loop=" + (Loop ? 1 : 0),
                    "mute=" + (Mute ? 1 : 0),
                    "preview=" + (Preview ? 1 : 0),
                });
            }
            catch { }
        }
    }

    internal static class Native
    {
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // Не даём компьютеру уснуть и погасить экран, пока идёт видео.
        public static void KeepAwake(bool on) =>
            SetThreadExecutionState(0x80000000 | (on ? 0x00000002u | 0x00000001u : 0u));

        // Тёмная рамка заголовка окна (Windows 10 1809+/11; на старых системах просто не сработает).
        public static void DarkTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            }
            catch { }
        }
    }

    public partial class MainWindow : Window
    {
        private static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 2 };

        private readonly MediaPlayer _player = new MediaPlayer { ScrubbingEnabled = true };
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly ObservableCollection<PlayItem> _items = new ObservableCollection<PlayItem>();
        private readonly VideoDrawing _drawing;
        private readonly DrawingBrush _previewBrush;
        private System.Windows.Forms.Screen[] _screens = System.Windows.Forms.Screen.AllScreens;
        private ProjectorWindow _proj;
        private PlayItem _current;
        private bool _playing, _loaded, _setting, _ready;
        private double _total;
        private int _speedIdx = 2;

        public MainWindow()
        {
            InitializeComponent();
            Settings.Load();

            // Один плеер и одна кисть на оба окна: пульт и проектор всегда синхронны.
            _drawing = new VideoDrawing { Player = _player, Rect = new Rect(0, 0, 16, 9) };
            _previewBrush = new DrawingBrush(_drawing) { Stretch = Stretch.Uniform };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(PreviewRect, BitmapScalingMode.LowQuality);
            PlayList.ItemsSource = _items;

            _player.MediaOpened += OnOpened;
            _player.MediaEnded += OnEnded;
            _player.MediaFailed += (s, e) => ShowHint("Не удалось открыть файл", _current?.Name ?? "", true);
            _timer.Tick += (s, e) => UpdateTime();

            VolSlider.Value = Settings.Volume;
            LoopChk.IsChecked = Settings.Loop;
            MuteChk.IsChecked = Settings.Mute;
            MuteChk.Content = Settings.Mute ? "🔇 Выкл" : "🔊";
            PreviewChk.IsChecked = Settings.Preview;
            _player.Volume = Settings.Volume;
            _player.IsMuted = Settings.Mute;
            PreviewRect.Fill = Settings.Preview ? _previewBrush : null;
            RefreshScreens(true);
            _ready = true;

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            // «Открыть с помощью» / перетаскивание файла на exe
            var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
            if (files.Length > 0) AddFiles(files, true);
        }

        private void Window_SourceInitialized(object sender, EventArgs e) =>
            Native.DarkTitleBar(new WindowInteropHelper(this).Handle);

        // ---------- Плейлист ----------

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Видео|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.webm;*.mpg;*.mpeg;*.ts;*.mts|Все файлы|*.*"
            };
            if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames, _current == null);
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                AddFiles(files.Where(File.Exists).ToArray(), _current == null);
        }

        private void AddFiles(string[] paths, bool playFirst)
        {
            PlayItem first = null;
            foreach (var p in paths)
            {
                var it = new PlayItem { Path = p };
                _items.Add(it);
                if (first == null) first = it;
            }
            if (playFirst && first != null) PlayItemNow(first, false);
        }

        private void PlayList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PlayList.SelectedItem is PlayItem it) PlayItemNow(it, true);
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _items.Clear();
            _current = null;
            _loaded = false;
            _player.Close();
            SetPlaying(false);
            ResetTimeUi();
            FileName.Text = "Vid-show";
            ShowHint("Перетащите видео сюда", "или нажмите «＋ Добавить» справа", false);
        }

        private void RemoveSelected()
        {
            if (PlayList.SelectedItem is PlayItem it)
            {
                int idx = PlayList.SelectedIndex;
                _items.Remove(it);
                if (it == _current) _current = null; // текущее продолжает играть, просто больше не в списке
                if (_items.Count > 0) PlayList.SelectedIndex = Math.Min(idx, _items.Count - 1);
            }
        }

        private void PlayItemNow(PlayItem it, bool autoplay)
        {
            if (_current != null) _current.IsPlaying = false;
            _current = it;
            it.IsPlaying = true;
            PlayList.SelectedItem = it;
            FileName.Text = it.Name;
            _loaded = false;
            ResetTimeUi();

            if (!File.Exists(it.Path))
            {
                SetPlaying(false);
                ShowHint("Файл не найден", it.Name, true);
                return;
            }
            HideHint();
            _player.Open(new Uri(it.Path));
            _player.Volume = VolSlider.Value;
            _player.IsMuted = MuteChk.IsChecked == true;
            _player.SpeedRatio = Speeds[_speedIdx];
            _player.Play(); // даже для «на паузе» — чтобы отрисовался первый кадр
            SetPlaying(autoplay);
        }

        private void Next_Click(object sender, RoutedEventArgs e) => Step(1);
        private void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);

        private void Step(int dir)
        {
            if (_items.Count == 0) return;
            if (dir < 0 && _loaded && _player.Position.TotalSeconds > 3) { SeekTo(0); return; }
            int i = _current == null ? 0 : _items.IndexOf(_current) + dir;
            if (i < 0) i = 0;
            if (i >= _items.Count) return;
            PlayItemNow(_items[i], _playing || _current == null);
        }

        // ---------- Воспроизведение ----------

        private void OnOpened(object sender, EventArgs e)
        {
            _loaded = true;
            _total = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 0;
            _setting = true;
            Timeline.Maximum = Math.Max(_total, 0.1);
            Timeline.Value = 0;
            _setting = false;
            _player.SpeedRatio = Speeds[_speedIdx];
            int w = _player.NaturalVideoWidth, h = _player.NaturalVideoHeight;
            if (w > 0 && h > 0) _drawing.Rect = new Rect(0, 0, w, h); // правильные пропорции кадра
            UpdateTime();
        }

        private void OnEnded(object sender, EventArgs e)
        {
            if (LoopChk.IsChecked == true)
            {
                _player.Position = TimeSpan.Zero;
                _player.Play();
                return;
            }
            int i = _current == null ? -1 : _items.IndexOf(_current);
            if (i >= 0 && i + 1 < _items.Count) { PlayItemNow(_items[i + 1], true); return; }
            _player.Position = TimeSpan.Zero;
            SetPlaying(false);
            UpdateTime();
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (!_loaded)
            {
                // ничего не загружено — запускаем выбранное/первое из плейлиста
                var it = PlayList.SelectedItem as PlayItem ?? _items.FirstOrDefault();
                if (it != null) PlayItemNow(it, true);
                return;
            }
            SetPlaying(!_playing);
        }

        private void SetPlaying(bool play)
        {
            _playing = play;
            if (play) _player.Play(); else _player.Pause();
            PlayBtn.Content = play ? "❚❚" : "▶";
            if (play) _timer.Start(); else _timer.Stop();
            Native.KeepAwake(play);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            SetPlaying(false);
            SeekTo(0);
        }

        private void SeekTo(double sec)
        {
            if (!_loaded) return;
            sec = Math.Max(0, _total > 0 ? Math.Min(sec, _total) : sec);
            _player.Position = TimeSpan.FromSeconds(sec);
            UpdateTime(sec);
        }

        private void UpdateTime(double? pos = null)
        {
            if (!_loaded) return;
            double p = pos ?? _player.Position.TotalSeconds;
            CurTime.Text = Fmt(p);
            RemTime.Text = "−" + Fmt(Math.Max(0, _total - p));
            if (!Timeline.IsMouseCaptureWithin)
            {
                _setting = true;
                Timeline.Value = Math.Min(p, Timeline.Maximum);
                _setting = false;
            }
        }

        private void ResetTimeUi()
        {
            _setting = true;
            Timeline.Value = 0;
            _setting = false;
            CurTime.Text = "00:00";
            RemTime.Text = "−00:00";
        }

        private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_setting || !_loaded) return;
            SeekTo(Timeline.Value);
        }

        private void Speed_Click(object sender, RoutedEventArgs e)
        {
            _speedIdx = (_speedIdx + 1) % Speeds.Length;
            _player.SpeedRatio = Speeds[_speedIdx];
            SpeedBtn.Content = Speeds[_speedIdx].ToString("0.##", CultureInfo.InvariantCulture) + "×";
        }

        // ---------- Опции ----------

        private void Vol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            _player.Volume = VolSlider.Value;
            Settings.Volume = VolSlider.Value;
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            _player.IsMuted = MuteChk.IsChecked == true;
            MuteChk.Content = _player.IsMuted ? "🔇 Выкл" : "🔊";
            Settings.Mute = _player.IsMuted;
        }

        private void Loop_Click(object sender, RoutedEventArgs e) => Settings.Loop = LoopChk.IsChecked == true;

        private void PreviewChk_Click(object sender, RoutedEventArgs e)
        {
            bool on = PreviewChk.IsChecked == true;
            PreviewRect.Fill = on ? _previewBrush : null;
            Settings.Preview = on;
        }

        private void Black_Click(object sender, RoutedEventArgs e)
        {
            bool on = BlackChk.IsChecked == true;
            BlackBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _proj?.SetBlackout(on);
        }

        // ---------- Проектор и мониторы ----------

        private void RefreshScreens(bool initial = false)
        {
            _screens = System.Windows.Forms.Screen.AllScreens;
            if (initial)
            {
                int idx = Settings.ScreenIdx;
                if (idx < 0 || idx >= _screens.Length || (_screens.Length > 1 && idx == MainScreenIndex()))
                    idx = Array.FindIndex(_screens, s => !s.Primary);
                _screenIdx = idx < 0 ? 0 : idx;
            }
            else if (_screenIdx >= _screens.Length) _screenIdx = 0;
            UpdateScreenUi();
        }

        private int _screenIdx;

        private int MainScreenIndex()
        {
            try
            {
                var cur = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
                return Array.FindIndex(_screens, s => s.DeviceName == cur.DeviceName);
            }
            catch { return 0; }
        }

        private void UpdateScreenUi()
        {
            var b = _screens[_screenIdx].Bounds;
            ScreenBtn.Content = $"Экран {_screenIdx + 1} · {b.Width}×{b.Height}";
        }

        private void Screen_Click(object sender, RoutedEventArgs e)
        {
            _screenIdx = (_screenIdx + 1) % _screens.Length;
            Settings.ScreenIdx = _screenIdx;
            UpdateScreenUi();
            if (_proj != null && _proj.IsVisible) ShowProjector();
        }

        private void OnDisplayChanged(object sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshScreens();
            if (_proj != null && _proj.IsVisible) ShowProjector();
        }));

        private void Proj_Click(object sender, RoutedEventArgs e) => ToggleProjector();

        private void ToggleProjector()
        {
            if (_proj != null && _proj.IsVisible) _proj.Hide();
            else ShowProjector();
        }

        private void ShowProjector()
        {
            if (_proj == null)
            {
                _proj = new ProjectorWindow(_previewBrush);
                _proj.IsVisibleChanged += (s, e) => UpdateProjUi();
            }
            var bounds = _screens[_screenIdx].Bounds;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (_screens.Length > 1 && MainScreenIndex() != _screenIdx) _proj.ShowFullscreen(bounds, dpi);
            else _proj.ShowWindowed(bounds, dpi);
            _proj.SetBlackout(BlackChk.IsChecked == true, false);
            UpdateProjUi();
            Activate(); // фокус остаётся на пульте
        }

        private void UpdateProjUi()
        {
            bool on = _proj != null && _proj.IsVisible;
            ProjBtn.Content = on ? "Скрыть проектор" : "Показать на проекторе";
            StatusDot.Fill = on ? (Brush)FindResource("LiveBrush") : (Brush)FindResource("MutedBrush");
            StatusText.Text = on ? "Идёт вывод на проектор" : "Проектор выключен";
        }

        // ---------- Подсказка поверх превью ----------

        private void ShowHint(string title, string sub, bool error)
        {
            HintText.Text = title;
            HintSub.Text = sub;
            HintText.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8D, 0xA1)) : (Brush)FindResource("TextBrush");
            Hint.Visibility = Visibility.Visible;
        }

        private void HideHint() => Hint.Visibility = Visibility.Collapsed;

        // ---------- Горячие клавиши ----------

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 30 : 5;
            bool handled = true;
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); break;
                case Key.B: BlackChk.IsChecked = BlackChk.IsChecked != true; Black_Click(null, null); break;
                case Key.F: ToggleProjector(); break;
                case Key.M: MuteChk.IsChecked = MuteChk.IsChecked != true; Mute_Click(null, null); break;
                case Key.L: LoopChk.IsChecked = LoopChk.IsChecked != true; Loop_Click(null, null); break;
                case Key.S: Stop_Click(null, null); break;
                case Key.N: case Key.PageDown: Step(1); break;
                case Key.P: case Key.PageUp: Step(-1); break;
                case Key.Left: if (_loaded) SeekTo(_player.Position.TotalSeconds - step); break;
                case Key.Right: if (_loaded) SeekTo(_player.Position.TotalSeconds + step); break;
                case Key.Home: SeekTo(0); break;
                case Key.Escape: if (_proj != null && _proj.IsVisible) _proj.Hide(); break;
                case Key.Delete: RemoveSelected(); break;
                case Key.Enter: if (PlayList.SelectedItem is PlayItem it) PlayItemNow(it, true); break;
                default: handled = false; break;
            }
            if (handled) e.Handled = true;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Settings.ScreenIdx = _screenIdx;
            Settings.Save();
            Native.KeepAwake(false);
            _proj?.ForceClose();
            _player.Close();
            Application.Current.Shutdown();
        }

        private static string Fmt(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }
    }
}
