using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VidShow
{
    public partial class MainWindow : Window
    {
        private readonly MediaPlayer _player = new MediaPlayer { ScrubbingEnabled = true };
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private ProjectorWindow _proj;
        private bool _playing;
        private bool _loaded;
        private bool _updatingSlider;

        public MainWindow()
        {
            InitializeComponent();

            PreviewRect.Fill = MakeBrush(_player);
            _player.MediaOpened += OnOpened;
            _player.MediaEnded += OnEnded;
            _player.MediaFailed += (s, e) => MessageBox.Show("Не удалось открыть файл:\n" + e.ErrorException?.Message);
            _timer.Tick += (s, e) => UpdateTime();
            _timer.Start();

            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                ScreenBox.Items.Add($"Экран {i + 1} ({b.Width}×{b.Height})" + (screens[i].Primary ? " — основной" : ""));
            }
            ScreenBox.SelectedIndex = screens.Length > 1 ? 1 : 0;
            if (screens.Length > 1)
            {
                // по умолчанию — первый неосновной экран
                for (int i = 0; i < screens.Length; i++)
                    if (!screens[i].Primary) { ScreenBox.SelectedIndex = i; break; }
            }
        }

        // Один MediaPlayer рисуется в нескольких местах — картинка на пульте и проекторе всегда синхронна.
        public static DrawingBrush MakeBrush(MediaPlayer player)
        {
            var drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, 1, 1) };
            return new DrawingBrush(drawing) { Stretch = Stretch.Uniform };
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Видео|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.webm;*.mpg;*.mpeg|Все файлы|*.*"
            };
            if (dlg.ShowDialog() == true) Load(dlg.FileName);
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) Load(files[0]);
        }

        private void Load(string path)
        {
            _loaded = false;
            _playing = false;
            PlayBtn.Content = "▶ Старт";
            FileName.Text = Path.GetFileName(path);
            _player.Open(new Uri(path));
            _player.Volume = VolSlider.Value;
            // Play+Pause, чтобы показался первый кадр
            _player.Play();
            _player.Pause();
        }

        private void OnOpened(object sender, EventArgs e)
        {
            _loaded = true;
            double total = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 1;
            _updatingSlider = true;
            Timeline.Maximum = Math.Max(total, 0.1);
            Timeline.Value = 0;
            _updatingSlider = false;
            TotTime.Text = Fmt(total);
            CurTime.Text = Fmt(0);
        }

        private void OnEnded(object sender, EventArgs e)
        {
            _player.Position = TimeSpan.Zero;
            if (LoopChk.IsChecked == true)
            {
                _player.Play();
            }
            else
            {
                _player.Pause();
                _playing = false;
                PlayBtn.Content = "▶ Старт";
            }
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (!_loaded) return;
            if (_playing) { _player.Pause(); PlayBtn.Content = "▶ Старт"; }
            else { _player.Play(); PlayBtn.Content = "❚❚ Пауза"; }
            _playing = !_playing;
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (!_loaded) return;
            _player.Pause();
            _player.Position = TimeSpan.Zero;
            _playing = false;
            PlayBtn.Content = "▶ Старт";
        }

        private void UpdateTime()
        {
            if (!_loaded) return;
            double pos = _player.Position.TotalSeconds;
            CurTime.Text = Fmt(pos);
            if (!Timeline.IsMouseCaptureWithin)
            {
                _updatingSlider = true;
                Timeline.Value = Math.Min(pos, Timeline.Maximum);
                _updatingSlider = false;
            }
        }

        private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingSlider || !_loaded) return;
            _player.Position = TimeSpan.FromSeconds(Timeline.Value);
            CurTime.Text = Fmt(Timeline.Value);
        }

        private void Vol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_player != null) _player.Volume = VolSlider.Value;
        }

        private void ScreenBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_proj != null && _proj.IsVisible) ShowProjector();
        }

        private void Proj_Click(object sender, RoutedEventArgs e)
        {
            if (_proj != null && _proj.IsVisible) { _proj.Hide(); ProjBtn.Content = "Показать на проекторе"; }
            else ShowProjector();
        }

        private void ShowProjector()
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            int idx = Math.Max(0, Math.Min(ScreenBox.SelectedIndex, screens.Length - 1));
            if (_proj == null) _proj = new ProjectorWindow(_player);
            _proj.ShowOn(screens[idx].Bounds, VisualTreeHelper.GetDpi(this).DpiScaleX);
            ProjBtn.Content = "Скрыть проектор";
            Activate(); // фокус остаётся на пульте
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; }
            else if (e.Key == Key.Left && _loaded) _player.Position = TimeSpan.FromSeconds(Math.Max(0, _player.Position.TotalSeconds - 5));
            else if (e.Key == Key.Right && _loaded) _player.Position = TimeSpan.FromSeconds(Math.Min(Timeline.Maximum, _player.Position.TotalSeconds + 5));
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _proj?.Close();
            _player.Close();
            Application.Current.Shutdown();
        }

        private static string Fmt(double s)
        {
            var t = TimeSpan.FromSeconds(s);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        }
    }
}
