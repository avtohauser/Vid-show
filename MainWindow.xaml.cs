using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VidShow
{
    public class PlayItem : INotifyPropertyChanged
    {
        private static readonly HashSet<string> ImgExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };

        private bool _playing, _hold;
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public bool IsImage => ImgExt.Contains(System.IO.Path.GetExtension(Path ?? ""));

        // Картинка ожидания: висит, пока оператор сам не включит следующее.
        public bool Hold
        {
            get => _hold;
            set { _hold = value; Notify(nameof(Hold)); Notify(nameof(Badge)); }
        }
        public string Badge => IsImage ? (Hold ? "∞" : "🖼") : "";
        public bool IsPlaying
        {
            get => _playing;
            set { _playing = value; Notify(nameof(IsPlaying)); }
        }

        public static PlayItem Create(string path, bool? hold = null)
        {
            var it = new PlayItem { Path = path };
            it._hold = hold ?? it.IsImage;
            return it;
        }

        private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public event PropertyChangedEventHandler PropertyChanged;
    }

    // Один «слой» воспроизведения: видео (MediaPlayer) или картинка с собственными часами.
    // Двух слоёв достаточно: пока играет один, второй заранее подгружает следующий элемент очереди.
    internal sealed class Slot
    {
        public readonly int Index;
        public readonly MediaPlayer Player = new MediaPlayer { ScrubbingEnabled = true };
        public readonly VideoDrawing Drawing;
        public readonly DrawingBrush VideoBrush;
        public readonly ImageBrush ImageBrush = new ImageBrush { Stretch = Stretch.Uniform };
        private readonly Stopwatch _sw = new Stopwatch();
        private double _imgBase;

        public PlayItem Item;
        public Brush Brush;
        public bool Ready, Failed;
        public double Total;

        public Slot(int index)
        {
            Index = index;
            Drawing = new VideoDrawing { Player = Player, Rect = new Rect(0, 0, 16, 9) };
            VideoBrush = new DrawingBrush(Drawing) { Stretch = Stretch.Uniform };
        }

        public bool IsImage => Item != null && Item.IsImage;
        public bool Timed => Item != null && (IsImage ? !Item.Hold && Total > 0 : Ready && Total > 0);

        public double Pos => IsImage ? _imgBase + _sw.Elapsed.TotalSeconds : Player.Position.TotalSeconds;

        public void Seek(double s)
        {
            if (Item == null) return;
            if (IsImage)
            {
                bool run = _sw.IsRunning;
                _imgBase = Math.Max(0, s);
                _sw.Reset();
                if (run) _sw.Start();
            }
            else Player.Position = TimeSpan.FromSeconds(Math.Max(0, s));
        }

        public void Play() { if (Item == null) return; if (IsImage) _sw.Start(); else Player.Play(); }
        public void Pause() { if (Item == null) return; if (IsImage) _sw.Stop(); else Player.Pause(); }

        public void Clear()
        {
            if (Item == null) return;
            if (!IsImage) Player.Close();
            ImageBrush.ImageSource = null;
            _sw.Reset();
            _imgBase = 0;
            Item = null;
            Brush = null;
            Ready = Failed = false;
            Total = 0;
        }

        public void Load(PlayItem it, int imgSecs)
        {
            Clear();
            Item = it;
            if (!File.Exists(it.Path)) { Failed = true; return; }
            if (it.IsImage)
            {
                try
                {
                    var uri = new Uri(it.Path);
                    int width = 0;
                    try { width = BitmapDecoder.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0].PixelWidth; }
                    catch { }
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = uri;
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    if (width > 2560) bi.DecodePixelWidth = 2560; // не раздуваем память на огромных картинках
                    bi.EndInit();
                    bi.Freeze();
                    ImageBrush.ImageSource = bi;
                    Brush = ImageBrush;
                    Total = imgSecs;
                    Ready = true;
                }
                catch { Failed = true; }
            }
            else
            {
                Brush = VideoBrush;
                Player.Volume = 0;
                Player.Open(new Uri(it.Path));
                Player.Play(); // Play+Pause — чтобы отрисовался первый кадр
                Player.Pause();
            }
        }
    }

    internal static class Settings
    {
        public static readonly double[] FadeOpts = { 0, 0.5, 1, 2, 3 };
        public static readonly int[] ImgOpts = { 5, 10, 15, 30, 60 };

        public static int ScreenIdx;
        public static double Volume = 0.8;
        public static bool Loop, Mute;
        public static bool Preview = true;
        public static int FadeIdx = 2;
        public static int ImgIdx = 1;

        private static string Dir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VidShow");
        private static string FilePath => System.IO.Path.Combine(Dir, "settings.txt");
        private static string QueuePath => System.IO.Path.Combine(Dir, "queue.txt");

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
                        case "fade": int.TryParse(v, out FadeIdx); break;
                        case "img": int.TryParse(v, out ImgIdx); break;
                    }
                }
            }
            catch { /* первый запуск или файл недоступен — значения по умолчанию */ }
            FadeIdx = Math.Max(0, Math.Min(FadeIdx, FadeOpts.Length - 1));
            ImgIdx = Math.Max(0, Math.Min(ImgIdx, ImgOpts.Length - 1));
        }

        public static void Save(IEnumerable<PlayItem> queue)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[]
                {
                    "screen=" + ScreenIdx,
                    "volume=" + Volume.ToString("0.00", CultureInfo.InvariantCulture),
                    "loop=" + (Loop ? 1 : 0),
                    "mute=" + (Mute ? 1 : 0),
                    "preview=" + (Preview ? 1 : 0),
                    "fade=" + FadeIdx,
                    "img=" + ImgIdx,
                });
                File.WriteAllLines(QueuePath, queue.Select(i => (i.Hold ? "H|" : "T|") + i.Path));
            }
            catch { }
        }

        public static List<PlayItem> LoadQueue()
        {
            var res = new List<PlayItem>();
            try
            {
                foreach (var line in File.ReadAllLines(QueuePath))
                    if (line.Length > 2 && line[1] == '|') res.Add(PlayItem.Create(line.Substring(2), line[0] == 'H'));
            }
            catch { }
            return res;
        }
    }

    internal static class Native
    {
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // Не даём компьютеру уснуть и погасить экран, пока идёт показ.
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
        private const string VideoImageFilter =
            "Видео и картинки|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.webm;*.mpg;*.mpeg;*.ts;*.mts;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|Все файлы|*.*";

        private readonly Slot[] _slots = { new Slot(0), new Slot(1) };
        private readonly double[] _op = new double[2];
        private readonly double[] _gain = new double[2];
        private readonly int[] _z = new int[2];
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly DispatcherTimer _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly Stopwatch _fadeSw = new Stopwatch();
        private readonly ObservableCollection<PlayItem> _items = new ObservableCollection<PlayItem>();
        private System.Windows.Forms.Screen[] _screens = System.Windows.Forms.Screen.AllScreens;
        private ProjectorWindow _proj;
        private Slot _act, _fadeFrom;
        private double _fadeDur;
        private bool _playing, _setting, _ready;
        private int _speedIdx = 2;
        private int _screenIdx;

        private double FadeSecs => Settings.FadeOpts[Settings.FadeIdx];
        private int ImgSecs => Settings.ImgOpts[Settings.ImgIdx];
        private Slot Other(Slot s) => _slots[1 - s.Index];

        public MainWindow()
        {
            InitializeComponent();
            Settings.Load();

            System.Windows.Media.RenderOptions.SetBitmapScalingMode(PrevA, BitmapScalingMode.LowQuality);
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(PrevB, BitmapScalingMode.LowQuality);
            PlayList.ItemsSource = _items;

            foreach (var slot in _slots)
            {
                var s = slot;
                s.Player.MediaOpened += (a, b) => OnOpened(s);
                s.Player.MediaEnded += (a, b) => OnEnded(s);
                s.Player.MediaFailed += (a, b) => OnFailed(s);
            }
            _timer.Tick += (s, e) => Tick();
            _fadeTimer.Tick += (s, e) => FadeTick();

            VolSlider.Value = Settings.Volume;
            LoopChk.IsChecked = Settings.Loop;
            MuteChk.IsChecked = Settings.Mute;
            MuteChk.Content = Settings.Mute ? "🔇 Выкл" : "🔊";
            PreviewChk.IsChecked = Settings.Preview;
            UpdateQueueButtons();
            RefreshScreens(true);
            _ready = true;
            ApplyVolume();

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

            // «Открыть с помощью» / перетаскивание файла на exe; иначе — очередь с прошлого раза
            var files = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
            if (files.Length > 0) AddFiles(files, true);
            else
            {
                foreach (var it in Settings.LoadQueue()) _items.Add(it);
                if (_items.Count > 0) ShowHint("Очередь загружена", "нажмите ▶ — начнётся с первого элемента", false);
            }
        }

        private void Window_SourceInitialized(object sender, EventArgs e) =>
            Native.DarkTitleBar(new WindowInteropHelper(this).Handle);

        // ---------- Слои: что видно на пульте и проекторе ----------

        private void RefreshLayers()
        {
            for (int i = 0; i < 2; i++)
            {
                var s = _slots[i];
                Brush b = s.Item == null || s.Failed ? null : s.Brush;
                var rect = i == 0 ? PrevA : PrevB;
                rect.Fill = Settings.Preview ? b : null;
                rect.Opacity = _op[i];
                Panel.SetZIndex(rect, _z[i]);
                _proj?.SetLayer(i, b, _op[i], _z[i]);
            }
        }

        private void UpdateOpacity()
        {
            PrevA.Opacity = _op[0];
            PrevB.Opacity = _op[1];
            _proj?.SetOpacity(0, _op[0]);
            _proj?.SetOpacity(1, _op[1]);
        }

        private void ApplyVolume()
        {
            double master = VolSlider.Value;
            bool mute = MuteChk.IsChecked == true;
            for (int i = 0; i < 2; i++)
            {
                _slots[i].Player.Volume = master * _gain[i];
                _slots[i].Player.IsMuted = mute;
            }
        }

        // ---------- Очередь ----------

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Multiselect = true, Filter = VideoImageFilter };
            if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames, _act == null);
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                AddFiles(files.Where(File.Exists).ToArray(), _act == null);
        }

        private void AddFiles(string[] paths, bool loadFirst)
        {
            PlayItem first = null;
            foreach (var p in paths)
            {
                var it = PlayItem.Create(p);
                _items.Add(it);
                if (first == null) first = it;
            }
            if (loadFirst && first != null) GoTo(first, false, false);
            else PreloadNext();
        }

        private void PlayList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PlayList.SelectedItem is PlayItem it) GoTo(it, true, true);
        }

        private void PlayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var it = PlayList.SelectedItem as PlayItem;
            HoldChk.IsEnabled = it != null && it.IsImage;
            HoldChk.IsChecked = it != null && it.Hold;
        }

        private void Hold_Click(object sender, RoutedEventArgs e)
        {
            if (!(PlayList.SelectedItem is PlayItem it) || !it.IsImage) return;
            it.Hold = HoldChk.IsChecked == true;
            foreach (var s in _slots)
                if (s.Item == it) s.Total = it.Hold ? 0 : ImgSecs;
            if (_act?.Item == it) UpdateTimeUi();
        }

        private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
        private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        private void MoveSelected(int d)
        {
            int i = PlayList.SelectedIndex, j = i + d;
            if (i < 0 || j < 0 || j >= _items.Count) return;
            _items.Move(i, j);
            PlayList.SelectedIndex = j;
            PreloadNext();
        }

        private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

        private void RemoveSelected()
        {
            if (!(PlayList.SelectedItem is PlayItem it)) return;
            int idx = PlayList.SelectedIndex;
            _items.Remove(it);
            if (_items.Count > 0) PlayList.SelectedIndex = Math.Min(idx, _items.Count - 1);
            PreloadNext(); // текущий элемент продолжает играть, даже если его убрали из списка
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            FinishTransition();
            foreach (var s in _slots) s.Clear();
            _act = null;
            _items.Clear();
            _op[0] = _op[1] = 0;
            _gain[0] = _gain[1] = 0;
            SetPlaying(false);
            ResetTimeUi();
            RefreshLayers();
            FileName.Text = "Vid-show";
            ShowHint("Перетащите видео и картинки сюда", "или нажмите «＋ Добавить» справа", false);
        }

        // Подгружаем следующий элемент очереди в свободный слой — чтобы переключение было мгновенным.
        private void PreloadNext()
        {
            if (_act == null || _fadeFrom != null) return;
            var idle = Other(_act);
            int i = _items.IndexOf(_act.Item);
            PlayItem next = i >= 0 && i + 1 < _items.Count ? _items[i + 1] : null;
            if (next == null) idle.Clear();
            else if (idle.Item != next) idle.Load(next, ImgSecs);
            RefreshLayers();
        }

        private bool HasNext()
        {
            int i = _act == null ? -1 : _items.IndexOf(_act.Item);
            return i >= 0 && i + 1 < _items.Count;
        }

        // ---------- Переключение элементов ----------

        // Включает элемент очереди. Если что-то уже идёт и переход включён — плавно проявляет новое поверх.
        private bool GoTo(PlayItem it, bool play, bool allowFade)
        {
            FinishTransition();
            var from = _act;
            var to = from == null ? _slots[0] : Other(from);
            bool preloaded = to.Item == it && !to.Failed;
            if (!preloaded) to.Load(it, ImgSecs);
            if (to.Failed)
            {
                to.Clear();
                if (from == null) ShowHint("Не удалось открыть файл", it.Name, true);
                return false;
            }
            if (preloaded) to.Seek(0);

            if (from?.Item != null) from.Item.IsPlaying = false;
            it.IsPlaying = true;
            _act = to;
            PlayList.SelectedItem = it;
            FileName.Text = it.Name;
            HideHint();
            to.Player.SpeedRatio = Speeds[_speedIdx];

            bool fade = allowFade && play && from?.Item != null && FadeSecs > 0;
            _z[to.Index] = 2;
            _z[from?.Index ?? 1 - to.Index] = 1;
            if (fade)
            {
                _fadeFrom = from;
                _fadeDur = FadeSecs;
                _op[to.Index] = 0; _op[from.Index] = 1;
                _gain[to.Index] = 0; _gain[from.Index] = 1;
                _fadeSw.Restart();
                _fadeTimer.Start();
            }
            else
            {
                from?.Clear();
                _op[to.Index] = 1; _op[1 - to.Index] = 0;
                _gain[to.Index] = 1; _gain[1 - to.Index] = 0;
            }
            RefreshLayers();
            ApplyVolume();
            SetPlaying(play);
            UpdateTimeUi();
            if (!fade) PreloadNext();
            return true;
        }

        private void FadeTick()
        {
            if (_fadeFrom == null) { _fadeTimer.Stop(); return; }
            double t = Math.Min(1, _fadeSw.Elapsed.TotalSeconds / _fadeDur);
            double e = t * t * (3 - 2 * t); // плавный старт и финиш
            _op[_act.Index] = e;
            _gain[_act.Index] = e;
            _gain[_fadeFrom.Index] = 1 - e;
            ApplyVolume();
            UpdateOpacity();
            if (t >= 1) FinishTransition();
        }

        private void FinishTransition()
        {
            if (_fadeFrom == null) return;
            var from = _fadeFrom;
            _fadeFrom = null;
            _fadeTimer.Stop();
            from.Clear();
            _op[from.Index] = 0; _op[_act.Index] = 1;
            _gain[from.Index] = 0; _gain[_act.Index] = 1;
            RefreshLayers();
            ApplyVolume();
            PreloadNext();
        }

        private bool AdvanceAuto()
        {
            int i = _act == null ? -1 : _items.IndexOf(_act.Item);
            if (i < 0) return false;
            for (i++; i < _items.Count; i++)
                if (GoTo(_items[i], true, true)) return true;
            return false;
        }

        private void Next_Click(object sender, RoutedEventArgs e) => Step(1);
        private void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);

        private void Step(int dir)
        {
            if (_items.Count == 0) return;
            if (dir < 0 && _act != null && !_act.IsImage && _act.Pos > 3) { Seek(0); return; }
            int i = _act == null ? 0 : _items.IndexOf(_act.Item) + dir;
            if (_act != null && _items.IndexOf(_act.Item) < 0) return;
            if (i < 0) i = 0;
            if (i >= _items.Count) return;
            bool wasHold = _act != null && _act.IsImage && _act.Item.Hold;
            GoTo(_items[i], _playing || _act == null || wasHold, true);
        }

        // ---------- Воспроизведение ----------

        private void OnOpened(Slot s)
        {
            if (s.Item == null) return;
            s.Ready = true;
            s.Total = s.Player.NaturalDuration.HasTimeSpan ? s.Player.NaturalDuration.TimeSpan.TotalSeconds : 0;
            int w = s.Player.NaturalVideoWidth, h = s.Player.NaturalVideoHeight;
            if (w > 0 && h > 0) s.Drawing.Rect = new Rect(0, 0, w, h); // правильные пропорции кадра
            s.Player.SpeedRatio = Speeds[_speedIdx];
            if (s == _act) UpdateTimeUi();
        }

        private void OnFailed(Slot s)
        {
            s.Failed = true;
            if (s != _act) return;
            ShowHint("Не удалось открыть файл", s.Item?.Name ?? "", true);
            if (_playing) AdvanceAuto();
        }

        private void OnEnded(Slot s)
        {
            if (s != _act) return;
            if (LoopChk.IsChecked == true)
            {
                s.Seek(0);
                s.Play();
                return;
            }
            if (!AdvanceAuto()) { SetPlaying(false); UpdateTime(); } // остаёмся на последнем кадре
        }

        private void Tick()
        {
            if (_act?.Item == null) return;
            UpdateTime();
            if (!_playing || _fadeFrom != null || !_act.Timed) return;

            var a = _act;
            double rem = a.Total - a.Pos;
            if (a.IsImage)
            {
                if (rem > 0) return;
                if (LoopChk.IsChecked == true) a.Seek(0);
                else if (!AdvanceAuto()) { SetPlaying(false); }
            }
            else if (FadeSecs > 0 && LoopChk.IsChecked != true && HasNext())
            {
                // начинаем переход заранее, чтобы проявление закончилось ровно с концом видео
                if (rem <= Math.Min(FadeSecs, a.Total * 0.5)) AdvanceAuto();
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (_act == null)
            {
                var it = PlayList.SelectedItem as PlayItem ?? _items.FirstOrDefault();
                if (it != null) GoTo(it, true, false);
                return;
            }
            // Картинка ожидания: «старт» = включить следующее из очереди
            if (_act.IsImage && _act.Item.Hold && HasNext()) { Step(1); return; }
            SetPlaying(!_playing);
        }

        private void SetPlaying(bool play)
        {
            _playing = play;
            foreach (var s in new[] { _act, _fadeFrom })
            {
                if (s?.Item == null) continue;
                if (play)
                {
                    if (!s.IsImage && s.Total > 0 && s.Pos >= s.Total - 0.05) s.Seek(0); // доиграно — начинаем сначала
                    s.Play();
                }
                else s.Pause();
            }
            PlayBtn.Content = play ? "❚❚" : "▶";
            if (play) _timer.Start(); else _timer.Stop();
            Native.KeepAwake(play);
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_act == null) return;
            FinishTransition();
            SetPlaying(false);
            Seek(0);
        }

        private void Seek(double sec)
        {
            var a = _act;
            if (a?.Item == null || (!a.IsImage && !a.Ready)) return;
            if (a.Total > 0) sec = Math.Min(sec, a.Total);
            a.Seek(sec);
            UpdateTime();
        }

        private void UpdateTimeUi()
        {
            var a = _act;
            _setting = true;
            Timeline.Maximum = a != null && a.Timed ? a.Total : 1;
            Timeline.Value = 0;
            Timeline.IsEnabled = a != null && a.Timed;
            _setting = false;
            UpdateTime();
        }

        private void UpdateTime()
        {
            var a = _act;
            if (a?.Item == null) return;
            double p = a.Pos;
            if (a.Timed)
            {
                CurTime.Text = Fmt(p);
                RemTime.Text = "−" + Fmt(Math.Max(0, a.Total - p));
                if (!Timeline.IsMouseCaptureWithin)
                {
                    _setting = true;
                    Timeline.Value = Math.Min(p, Timeline.Maximum);
                    _setting = false;
                }
            }
            else
            {
                CurTime.Text = a.IsImage ? Fmt(p) : "00:00";
                RemTime.Text = a.IsImage ? "∞" : "";
            }
        }

        private void ResetTimeUi()
        {
            _setting = true;
            Timeline.Value = 0;
            Timeline.IsEnabled = true;
            _setting = false;
            CurTime.Text = "00:00";
            RemTime.Text = "−00:00";
        }

        private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_setting || _act == null || !_act.Timed) return;
            Seek(Timeline.Value);
        }

        private void Speed_Click(object sender, RoutedEventArgs e)
        {
            _speedIdx = (_speedIdx + 1) % Speeds.Length;
            foreach (var s in _slots) s.Player.SpeedRatio = Speeds[_speedIdx];
            SpeedBtn.Content = Speeds[_speedIdx].ToString("0.##", CultureInfo.InvariantCulture) + "×";
        }

        // ---------- Опции ----------

        private void UpdateQueueButtons()
        {
            FadeBtn.Content = FadeSecs <= 0 ? "Резкий" : FadeSecs.ToString("0.#", CultureInfo.InvariantCulture) + " с";
            ImgBtn.Content = ImgSecs + " с";
        }

        private void Fade_Click(object sender, RoutedEventArgs e)
        {
            Settings.FadeIdx = (Settings.FadeIdx + 1) % Settings.FadeOpts.Length;
            UpdateQueueButtons();
        }

        private void ImgSecs_Click(object sender, RoutedEventArgs e)
        {
            Settings.ImgIdx = (Settings.ImgIdx + 1) % Settings.ImgOpts.Length;
            UpdateQueueButtons();
            foreach (var s in _slots)
                if (s.IsImage && !s.Item.Hold) s.Total = ImgSecs;
            if (_act != null && _act.IsImage) UpdateTimeUi();
        }

        private void Vol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_ready) return;
            Settings.Volume = VolSlider.Value;
            ApplyVolume();
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            bool mute = MuteChk.IsChecked == true;
            MuteChk.Content = mute ? "🔇 Выкл" : "🔊";
            Settings.Mute = mute;
            ApplyVolume();
        }

        private void Loop_Click(object sender, RoutedEventArgs e) => Settings.Loop = LoopChk.IsChecked == true;

        private void PreviewChk_Click(object sender, RoutedEventArgs e)
        {
            Settings.Preview = PreviewChk.IsChecked == true;
            RefreshLayers();
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
                _proj = new ProjectorWindow();
                _proj.IsVisibleChanged += (s, e) => UpdateProjUi();
            }
            var bounds = _screens[_screenIdx].Bounds;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (_screens.Length > 1 && MainScreenIndex() != _screenIdx) _proj.ShowFullscreen(bounds, dpi);
            else _proj.ShowWindowed(bounds, dpi);
            RefreshLayers();
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
                case Key.Left: if (_act != null) Seek(_act.Pos - step); break;
                case Key.Right: if (_act != null) Seek(_act.Pos + step); break;
                case Key.Home: Seek(0); break;
                case Key.Escape: if (_proj != null && _proj.IsVisible) _proj.Hide(); break;
                case Key.Delete: RemoveSelected(); break;
                case Key.Enter: if (PlayList.SelectedItem is PlayItem it) GoTo(it, true, true); break;
                default: handled = false; break;
            }
            if (handled) e.Handled = true;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Settings.ScreenIdx = _screenIdx;
            Settings.Save(_items);
            Native.KeepAwake(false);
            _proj?.ForceClose();
            foreach (var s in _slots) s.Clear();
            Application.Current.Shutdown();
        }

        private static string Fmt(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }
    }
}
