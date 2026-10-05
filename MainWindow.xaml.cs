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
        public double EndPos => Item == null ? 0 : (IsImage || Item.Out <= 0 || Item.Out >= Total ? Total : Item.Out);
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
            if (it.IsBlack)
            {
                Brush = System.Windows.Media.Brushes.Black;
                Total = it.Secs > 0 ? it.Secs : imgSecs;
                Ready = true;
                return;
            }
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
                    Total = it.Secs > 0 ? it.Secs : imgSecs;
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
        public static double MusicVol = 0.7;
        public static int MusicLoop = 1;      // 0 — один раз, 1 — список по кругу, 2 — один трек по кругу
        public static bool MusicShuffle;
        public static bool Duck = true;
        public static string AudioDevice = "";
        public static double SfxVol = 0.9;
        public static readonly string[] Pads = new string[6];

        private static string Dir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VidShow");
        private static string FilePath => System.IO.Path.Combine(Dir, "settings.txt");
        private static string QueuePath => System.IO.Path.Combine(Dir, "queue.txt");
        private static string MusicPath => System.IO.Path.Combine(Dir, "music.txt");

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
                        case "mvol": double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out MusicVol); break;
                        case "mloop": int.TryParse(v, out MusicLoop); break;
                        case "mshuffle": MusicShuffle = v == "1"; break;
                        case "duck": Duck = v != "0"; break;
                        case "device": AudioDevice = v; break;
                        case "sfxvol": double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out SfxVol); break;
                        default:
                            if (p[0].StartsWith("pad") && int.TryParse(p[0].Substring(3), out int pi) && pi >= 0 && pi < Pads.Length)
                                Pads[pi] = v.Length > 0 ? v : null;
                            break;
                    }
                }
            }
            catch { /* первый запуск или файл недоступен — значения по умолчанию */ }
            FadeIdx = Math.Max(0, Math.Min(FadeIdx, FadeOpts.Length - 1));
            ImgIdx = Math.Max(0, Math.Min(ImgIdx, ImgOpts.Length - 1));
            MusicVol = Math.Max(0, Math.Min(1, MusicVol));
            MusicLoop = Math.Max(0, Math.Min(2, MusicLoop));
            SfxVol = Math.Max(0, Math.Min(1, SfxVol));
        }

        public static void Save(IEnumerable<PlayItem> queue, IEnumerable<PlayItem> music)
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
                    "mvol=" + MusicVol.ToString("0.00", CultureInfo.InvariantCulture),
                    "mloop=" + MusicLoop,
                    "mshuffle=" + (MusicShuffle ? 1 : 0),
                    "duck=" + (Duck ? 1 : 0),
                    "device=" + AudioDevice,
                    "sfxvol=" + SfxVol.ToString("0.00", CultureInfo.InvariantCulture),
                }.Concat(Pads.Select((pd, k) => "pad" + k + "=" + (pd ?? ""))));
                WriteList(MusicPath, music);
                WriteList(QueuePath, queue);
            }
            catch { }
        }

        public static List<PlayItem> LoadQueue() => ReadList(QueuePath);
        public static List<PlayItem> LoadMusic() => ReadList(MusicPath);

        public static void WriteList(string file, IEnumerable<PlayItem> items) =>
            File.WriteAllLines(file, items.Where(i => !i.IsVirtual).Select(i => i.ToLine()));

        public static List<PlayItem> ReadList(string file)
        {
            var res = new List<PlayItem>();
            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    var it = PlayItem.FromLine(line);
                    if (it != null) res.Add(it);
                }
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
        private readonly Filmstrip _film = new Filmstrip();
        private readonly Image[] _tiles = new Image[Filmstrip.Count];
        private bool _playing, _ready;
        private int _speedIdx = 2;
        private int _loopsDone, _virtualNext;
        private bool _waitNext, _fadeViaBlack;
        private ProgramWindow _program;
        private Prober _prober;
        private int _screenIdx;

        private double FadeSecs => Settings.FadeOpts[Settings.FadeIdx];
        private int ImgSecs => Settings.ImgOpts[Settings.ImgIdx];
        private Slot Other(Slot s) => _slots[1 - s.Index];

        public MainWindow()
        {
            InitializeComponent();
            Settings.Load();
            _prober = new Prober(() => _program?.Rebuild());

            System.Windows.Media.RenderOptions.SetBitmapScalingMode(PrevA, BitmapScalingMode.LowQuality);
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(PrevB, BitmapScalingMode.LowQuality);
            PlayList.ItemsSource = _items;
            Tiles.Columns = Filmstrip.Count;
            for (int i = 0; i < _tiles.Length; i++)
            {
                _tiles[i] = new Image { Stretch = Stretch.UniformToFill, SnapsToDevicePixels = true };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(_tiles[i], BitmapScalingMode.LowQuality);
                Tiles.Children.Add(_tiles[i]);
            }

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
            InitMusic();
            var cmdAudio = files.Where(PlayItem.IsAudioPath).ToArray();
            files = files.Where(f => !PlayItem.IsAudioPath(f)).ToArray();
            if (cmdAudio.Length > 0) AddMusic(cmdAudio);
            if (files.Length > 0) AddFiles(files, true);
            else
            {
                foreach (var it in Settings.LoadQueue()) _items.Add(it);
                _prober.Enqueue(_items);
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
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
            files = files.Where(File.Exists).ToArray();
            var audio = files.Where(PlayItem.IsAudioPath).ToArray();
            var media = files.Where(f => !PlayItem.IsAudioPath(f)).ToArray();
            if (audio.Length > 0) AddMusic(audio);
            if (media.Length > 0) AddFiles(media, _act == null);
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
            _prober?.Enqueue(_items);
            if (loadFirst && first != null) GoTo(first, false, false);
            else PreloadNext();
            _program?.Rebuild();
        }

        private void PlayList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PlayList.SelectedItem is PlayItem it) GoTo(it, true, true);
        }

        private void PlayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var it = PlayList.SelectedItem as PlayItem;
            HoldChk.IsEnabled = it != null;
            HoldChk.IsChecked = it != null && it.Hold;
        }

        private void Hold_Click(object sender, RoutedEventArgs e)
        {
            if (!(PlayList.SelectedItem is PlayItem it)) return;
            it.Hold = HoldChk.IsChecked == true;
            AfterEdit(it);
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
            _program?.Rebuild();
        }

        private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

        private void RemoveSelected()
        {
            if (!(PlayList.SelectedItem is PlayItem it)) return;
            int idx = PlayList.SelectedIndex;
            _items.Remove(it);
            if (_items.Count > 0) PlayList.SelectedIndex = Math.Min(idx, _items.Count - 1);
            PreloadNext();
            _program?.Rebuild(); // текущий элемент продолжает играть, даже если его убрали из списка
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
            _program?.Rebuild();
        }

        // ---------- Свойства элемента, чёрный экран, проект, программа ----------

        private void EditSelected()
        {
            if (PlayList.SelectedItem is PlayItem it) EditCue(it);
        }

        private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

        public void EditCue(PlayItem it)
        {
            var dlg = new CueWindow(it, GetPosFor) { Owner = this };
            if (dlg.ShowDialog() == true) AfterEdit(it);
        }

        // Текущая позиция плеера для элемента (чтобы «взять In/Out с плеера»); null — элемент сейчас не играет
        private double? GetPosFor(PlayItem it) => _act?.Item == it && !_act.IsImage ? (double?)_act.Pos : null;

        private void AfterEdit(PlayItem it)
        {
            it.Refresh();
            foreach (var s in _slots)
                if (s.Item == it && s.IsImage) s.Total = it.Secs > 0 ? it.Secs : ImgSecs;
            if (_act?.Item == it) UpdateTimeUi();
            PlayList_SelectionChanged(null, null);
            PreloadNext();
            _program?.Rebuild();
        }

        private void AddBlack_Click(object sender, RoutedEventArgs e)
        {
            var it = PlayItem.CreateBlack();
            int idx = PlayList.SelectedIndex >= 0 ? PlayList.SelectedIndex + 1 : _items.Count;
            _items.Insert(idx, it);
            PlayList.SelectedItem = it;
            PreloadNext();
            _program?.Rebuild();
        }

        private void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "Программа Vid-show|*.vshow", FileName = "программа.vshow" };
            if (dlg.ShowDialog() != true) return;
            try { Settings.WriteList(dlg.FileName, _items); }
            catch (Exception ex) { MessageBox.Show("Не удалось сохранить: " + ex.Message); }
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Программа Vid-show|*.vshow" };
            if (dlg.ShowDialog() != true) return;
            if (_items.Count > 0 &&
                MessageBox.Show("Заменить текущую очередь программой из файла?", "Vid-show", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var list = Settings.ReadList(dlg.FileName);
            Clear_Click(null, null);
            foreach (var it in list) _items.Add(it);
            _prober.Enqueue(_items);
            if (_items.Count > 0) GoTo(_items[0], false, false);
            _program?.Rebuild();
        }

        private void Program_Click(object sender, RoutedEventArgs e) => OpenProgram();

        private void OpenProgram()
        {
            if (_program == null)
            {
                _program = new ProgramWindow(_items, it => PlayList.SelectedItem = it, it => GoTo(it, true, true), EditCue,
                    () => { PreloadNext(); }, () => _act?.Item, () => _act != null && _act.Timed && !_act.IsImage ? _act.Pos / _act.Total : 0);
                _program.Owner = this;
                _program.Closed += (s, a) => _program = null;
            }
            _program.Show();
            _program.Activate();
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
            if (preloaded && !to.IsImage) to.Seek(it.In);
            else if (preloaded) to.Seek(0);

            if (from?.Item != null) from.Item.IsPlaying = false;
            it.IsPlaying = true;
            _act = to;
            _loopsDone = 0;
            _waitNext = false;
            if (!it.IsVirtual) PlayList.SelectedItem = it;
            FileName.Text = it.Name;
            HideHint();
            to.Player.SpeedRatio = Speeds[_speedIdx];

            double fs = it.Fade >= 0 ? it.Fade : FadeSecs;
            bool fade = allowFade && play && from?.Item != null && fs > 0;
            _z[to.Index] = 2;
            _z[from?.Index ?? 1 - to.Index] = 1;
            if (fade)
            {
                _fadeFrom = from;
                _fadeDur = fs;
                _fadeViaBlack = it.ViaBlack;
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
            if (play) RunCueActions(it);
            _program?.Rebuild();
            return true;
        }

        // Фанфара и действия с музыкой, привязанные к моменту старта элемента
        private void RunCueActions(PlayItem it)
        {
            if (!string.IsNullOrEmpty(it.Sfx)) PlaySfx(it.Sfx);
            if (it.MusicAct == 1) MusicStartAction();
            else if (it.MusicAct == 2) MusicFadeOut_Click(null, null);
        }

        private void FadeTick()
        {
            if (_fadeFrom == null) { _fadeTimer.Stop(); return; }
            double t = Math.Min(1, _fadeSw.Elapsed.TotalSeconds / _fadeDur);
            double e = t * t * (3 - 2 * t); // плавный старт и финиш
            double toOp, fromOp, toGain, fromGain;
            if (_fadeViaBlack)
            {
                // сначала старое уходит в чёрное, потом из чёрного проявляется новое
                double a = Math.Min(1, e * 2), b = Math.Max(0, e * 2 - 1);
                fromOp = fromGain = 1 - a;
                toOp = toGain = b;
            }
            else
            {
                toOp = toGain = e;
                fromOp = 1;
                fromGain = 1 - e;
            }
            _op[_act.Index] = toOp; _op[_fadeFrom.Index] = fromOp;
            _gain[_act.Index] = toGain; _gain[_fadeFrom.Index] = fromGain;
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

        // Оператор должен сам нажать «дальше»: картинка/чёрный с «ждать», зацикленное видео, видео доиграло и ждёт
        private bool WaitingForOperator =>
            _act?.Item != null && (_waitNext || _act.Item.IsVirtual || (_act.IsImage && _act.Item.Hold) ||
                                   (!_act.IsImage && _act.Item.Loops == 0));

        private bool HasNext()
        {
            if (_act?.Item == null) return false;
            if (_act.Item.IsVirtual) return _virtualNext < _items.Count;
            int i = _items.IndexOf(_act.Item);
            return i >= 0 && i + 1 < _items.Count;
        }

        private void Step(int dir)
        {
            if (_items.Count == 0) return;
            if (dir < 0 && _act?.Item != null && !_act.IsImage && _act.Pos > _act.Item.In + 3) { Seek(_act.Item.In); return; }
            int i;
            if (_act == null) i = 0;
            else if (_act.Item.IsVirtual) { if (dir < 0) return; i = _virtualNext; }
            else
            {
                int cur = _items.IndexOf(_act.Item);
                if (cur < 0) return;
                i = cur + dir;
            }
            if (i < 0) i = 0;
            if (i >= _items.Count) return;
            bool waiting = _act == null || WaitingForOperator;
            GoTo(_items[i], _playing || waiting, true);
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
            if (s.Total > 0 && s.Item.Duration <= 0) { s.Item.Duration = s.Total; s.Item.Refresh(); _program?.Rebuild(); }
            if (s.Item.In > 0 && s.Item.In < s.Total) s.Player.Position = TimeSpan.FromSeconds(s.Item.In);
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
            OnItemFinished();
        }

        // Элемент доиграл (или дошёл до точки «Out»): повторить, затемнить, ждать оператора или идти дальше.
        private void OnItemFinished()
        {
            var a = _act;
            if (a?.Item == null || _waitNext) return;
            var it = a.Item;
            bool loopOne = LoopChk.IsChecked == true;
            if (a.IsImage)
            {
                if (loopOne) { a.Seek(0); return; }
            }
            else if (loopOne || it.Loops == 0 || _loopsDone + 1 < it.Loops)
            {
                _loopsDone++;
                a.Seek(it.In);
                if (_playing) a.Play();
                return;
            }

            if (it.EndBlack)
            {
                var b = PlayItem.CreateBlack();
                b.IsVirtual = true;
                _virtualNext = _items.IndexOf(it) + 1;
                GoTo(b, true, true);
                return;
            }
            if (it.Hold)
            {
                SetPlaying(false); // остаёмся на последнем кадре и ждём оператора
                _waitNext = true;
                return;
            }
            if (!AdvanceAuto()) SetPlaying(false); // конец очереди — остаёмся на последнем кадре
        }

        // Уйдёт ли программа с этого элемента сама (после последнего повтора)?
        private bool WillLeave(PlayItem it)
        {
            if (LoopChk.IsChecked == true || it.Loops == 0 || _loopsDone + 1 < it.Loops || it.Hold) return false;
            return it.EndBlack || HasNext();
        }

        private double LeadFade(PlayItem it)
        {
            if (it.EndBlack) return FadeSecs;
            int i = _items.IndexOf(it);
            if (i < 0 || i + 1 >= _items.Count) return 0;
            var n = _items[i + 1];
            return n.Fade >= 0 ? n.Fade : FadeSecs;
        }

        private void Tick()
        {
            var a = _act;
            if (a?.Item == null) return;
            UpdateTime();
            if (!_playing || _fadeFrom != null || !a.Timed) return;

            if (a.IsImage)
            {
                if (a.Total - a.Pos <= 0) OnItemFinished();
                return;
            }
            double rem = a.EndPos - a.Pos;
            if (rem <= 0.03) { OnItemFinished(); return; }
            if (WillLeave(a.Item))
            {
                // начинаем переход заранее, чтобы проявление закончилось ровно с концом видео
                double lead = Math.Min(LeadFade(a.Item), (a.EndPos - a.Item.In) * 0.5);
                if (lead > 0 && rem <= lead) OnItemFinished();
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay(false);

        private void TogglePlay(bool pureToggle)
        {
            if (_act == null)
            {
                var it = PlayList.SelectedItem as PlayItem ?? _items.FirstOrDefault();
                if (it != null) GoTo(it, true, false);
                return;
            }
            // «Старт» на картинке ожидания / зацикленном видео = включить следующее из очереди
            if (!pureToggle && WaitingForOperator && HasNext()) { Step(1); return; }
            if (_waitNext) { _waitNext = false; a_Restart(); return; }
            SetPlaying(!_playing);
        }

        // После «ждать» без следующего элемента: проиграть сначала
        private void a_Restart()
        {
            _loopsDone = 0;
            _act?.Seek(_act.Item?.In ?? 0);
            SetPlaying(true);
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
            UpdateAwake();
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
            UpdateTime();
            UpdateTrim();
            LoadFilm();
        }

        // Затемняем на таймлайне части, которые не войдут в показ (до «In» и после «Out»)
        private void UpdateTrim()
        {
            var a = _act;
            double w = FilmArea.ActualWidth;
            if (a?.Item == null || a.IsImage || !a.Timed || a.Total <= 0) { TrimL.Width = 0; TrimR.Width = 0; return; }
            TrimL.Width = Math.Max(0, Math.Min(1, a.Item.In / a.Total)) * w;
            TrimR.Width = Math.Max(0, Math.Min(1, 1 - a.EndPos / a.Total)) * w;
        }

        // Раскадровка таймлайна: для видео — кадры по ходу ролика, для картинки — она сама.
        private void LoadFilm()
        {
            var a = _act;
            if (a?.Item == null) { ClearFilm(); return; }
            if (a.IsImage)
            {
                _film.Cancel();
                var src = a.ImageBrush.ImageSource;
                foreach (var t in _tiles) t.Source = src;
                return;
            }
            if (!Settings.Preview || !a.Ready || a.Total <= 0) { if (_film.Path != a.Item.Path) ClearFilm(); return; }
            if (_film.Path == a.Item.Path) return;
            foreach (var t in _tiles) t.Source = null;
            _film.Start(a.Item.Path, a.Total, (i, bmp) => { if (i < _tiles.Length) _tiles[i].Source = bmp; });
        }

        private void ClearFilm()
        {
            _film.Cancel();
            foreach (var t in _tiles) t.Source = null;
        }

        private void UpdateTime()
        {
            var a = _act;
            if (a?.Item == null) return;
            double p = a.Pos;
            if (a.Timed)
            {
                CurTime.Text = Fmt(p);
                RemTime.Text = "−" + Fmt(Math.Max(0, a.EndPos - p));
                SetHead(p / a.Total);
            }
            else
            {
                CurTime.Text = a.IsImage ? Fmt(p) : "00:00";
                RemTime.Text = a.IsImage ? "∞" : "";
                SetHead(-1);
            }
        }

        private void SetHead(double frac)
        {
            if (frac < 0)
            {
                FilmHead.Visibility = Visibility.Collapsed;
                FilmDim.Width = 0;
                return;
            }
            frac = Math.Min(1, frac);
            double w = FilmArea.ActualWidth;
            FilmHead.Visibility = Visibility.Visible;
            FilmHead.Margin = new Thickness(Math.Max(0, Math.Min(w - 3, frac * w - 1.5)), 0, 0, 0);
            FilmDim.Width = Math.Max(0, (1 - frac) * w);
        }

        private void ResetTimeUi()
        {
            ClearFilm();
            SetHead(-1);
            CurTime.Text = "00:00";
            RemTime.Text = "−00:00";
        }

        private void Film_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Film.Clip = new RectangleGeometry(new Rect(0, 0, Film.ActualWidth, Film.ActualHeight), 8, 8);
            UpdateTime();
            UpdateTrim();
        }

        private double FilmFrac(MouseEventArgs e) =>
            Math.Max(0, Math.Min(1, e.GetPosition(FilmArea).X / Math.Max(1, FilmArea.ActualWidth)));

        private void Film_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_act == null || !_act.Timed) return;
            Film.CaptureMouse();
            Seek(FilmFrac(e) * _act.Total);
        }

        private void Film_MouseMove(object sender, MouseEventArgs e)
        {
            if (_act == null || !_act.Timed) { HoverTip.Visibility = Visibility.Collapsed; return; }
            double f = FilmFrac(e);
            HoverText.Text = Fmt(f * _act.Total);
            HoverTip.Visibility = Visibility.Visible;
            double x = f * FilmArea.ActualWidth - 20;
            HoverTip.Margin = new Thickness(Math.Max(0, Math.Min(FilmArea.ActualWidth - 50, x)), 4, 0, 0);
            if (Film.IsMouseCaptured) Seek(f * _act.Total);
        }

        private void Film_MouseUp(object sender, MouseButtonEventArgs e) => Film.ReleaseMouseCapture();
        private void Film_MouseLeave(object sender, MouseEventArgs e) => HoverTip.Visibility = Visibility.Collapsed;

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
                if (s.IsImage && s.Item.Secs <= 0) s.Total = ImgSecs;
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
            LoadFilm();
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
                case Key.K: MusicPlayPause_Click(null, null); break;
                case Key.Up when (Keyboard.Modifiers & ModifierKeys.Control) != 0: MusicVol.Value = Math.Min(1, MusicVol.Value + 0.05); break;
                case Key.Down when (Keyboard.Modifiers & ModifierKeys.Control) != 0: MusicVol.Value = Math.Max(0, MusicVol.Value - 0.05); break;
                case Key.Space: TogglePlay((Keyboard.Modifiers & ModifierKeys.Shift) != 0); break;
                case Key.E: EditSelected(); break;
                case Key.G: OpenProgram(); break;
                case Key.D1: case Key.NumPad1: PadFire(0); break;
                case Key.D2: case Key.NumPad2: PadFire(1); break;
                case Key.D3: case Key.NumPad3: PadFire(2); break;
                case Key.D4: case Key.NumPad4: PadFire(3); break;
                case Key.D5: case Key.NumPad5: PadFire(4); break;
                case Key.D6: case Key.NumPad6: PadFire(5); break;
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
                case Key.Delete: if (MusicTab.Visibility == Visibility.Visible) RemoveSelectedMusic(); else RemoveSelected(); break;
                case Key.Enter:
                    if (MusicTab.Visibility == Visibility.Visible) { if (MusicList.SelectedItem is PlayItem mi) PlayMusic(mi, true, true); }
                    else if (PlayList.SelectedItem is PlayItem it) GoTo(it, true, true);
                    break;
                default: handled = false; break;
            }
            if (handled) e.Handled = true;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Settings.ScreenIdx = _screenIdx;
            Settings.Save(_items, _music);
            Native.KeepAwake(false);
            _mPlayer.Dispose();
            AudioPlayer.StopAllOnce();
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
