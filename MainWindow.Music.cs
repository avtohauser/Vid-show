using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VidShow
{
    // Фоновая музыка: свой плеер и свой плейлист, не зависит от очереди видео.
    // Играет по кругу, плавно включается/выключается и сама затихает под видео со звуком.
    public partial class MainWindow
    {
        private const string AudioFilter = "Музыка|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac;*.ogg;*.opus|Все файлы|*.*";
        private static readonly string[] LoopNames = { "➡ Один раз", "🔁 Список", "🔂 Трек" };

        private readonly AudioPlayer _mPlayer = new AudioPlayer();
        private List<string> _devices = new List<string>();
        private int _devIdx = -1; // -1 — по умолчанию
        private int _sfxActive;
        private readonly ObservableCollection<PlayItem> _music = new ObservableCollection<PlayItem>();
        private readonly DispatcherTimer _mTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly Random _rnd = new Random();
        private PlayItem _mcur;
        private bool _mPlaying, _mOpened, _mSet, _mReady, _mPauseAtZero;
        private double _mGain = 1, _mFade = 1, _mFadeTarget = 1, _mFadeDur = 1.5;
        private int _mTick, _mFails;

        private void InitMusic()
        {
            MusicList.ItemsSource = _music;
            MusicVol.Value = Settings.MusicVol;
            MusicShuffleChk.IsChecked = Settings.MusicShuffle;
            DuckChk.IsChecked = Settings.Duck;
            MusicLoopBtn.Content = LoopNames[Settings.MusicLoop];

            _mPlayer.Ended += OnMusicEnded;
            _mPlayer.Failed += OnMusicFailed;
            RefreshDevices();
            SfxVol.Value = Settings.SfxVol;
            BuildPads();
            _mTimer.Tick += (s, e) => MusicTick();
            _mTimer.Start();

            foreach (var it in Settings.LoadMusic()) _music.Add(it);
            _mReady = true;
            if (_music.Count > 0) PlayMusic(_music[0], false, false); // подготовили, но не играем
        }

        private void UpdateAwake() => Native.KeepAwake(_playing || _mPlaying);

        // ---------- Вкладки ----------

        private void TabVideo_Click(object sender, RoutedEventArgs e) => ShowTab(false);
        private void TabMusic_Click(object sender, RoutedEventArgs e) => ShowTab(true);

        private void ShowTab(bool music)
        {
            VideoTab.Visibility = music ? Visibility.Collapsed : Visibility.Visible;
            MusicTab.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
            TabVideo.IsChecked = !music;
            TabMusic.IsChecked = music;
        }

        // ---------- Список ----------

        private void MusicAdd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Multiselect = true, Filter = AudioFilter };
            if (dlg.ShowDialog() == true) AddMusic(dlg.FileNames);
        }

        private void AddMusic(string[] paths)
        {
            foreach (var p in paths) _music.Add(PlayItem.Create(p, false));
            if (_mcur == null && _music.Count > 0) PlayMusic(_music[0], false, false);
            ShowTab(true);
        }

        private void MusicList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (MusicList.SelectedItem is PlayItem it) PlayMusic(it, true, true);
        }

        private void RemoveSelectedMusic()
        {
            if (!(MusicList.SelectedItem is PlayItem it)) return;
            int idx = MusicList.SelectedIndex;
            _music.Remove(it);
            if (_music.Count > 0) MusicList.SelectedIndex = Math.Min(idx, _music.Count - 1);
        }

        private void MusicClear_Click(object sender, RoutedEventArgs e)
        {
            _mPlayer.Close();
            _music.Clear();
            _mcur = null;
            _mOpened = false;
            SetMusicPlaying(false);
            MiniTitle.Text = "♪ Музыка не выбрана";
            MCur.Text = MTot.Text = "00:00";
            _mSet = true; MPos.Value = 0; _mSet = false;
        }

        // ---------- Воспроизведение ----------

        private void PlayMusic(PlayItem it, bool play, bool fadeIn)
        {
            if (_mcur != null) _mcur.IsPlaying = false;
            _mcur = it;
            it.IsPlaying = true;
            MusicList.SelectedItem = it;
            _mOpened = false;
            MiniTitle.Text = "♪ " + it.Name;
            if (!File.Exists(it.Path)) { MiniTitle.Text = "♪ Файл не найден: " + it.Name; SetMusicPlaying(false); return; }
            if (!_mPlayer.Open(it.Path)) { OnMusicFailed(); return; }
            _mPlayer.Loop = Settings.MusicLoop == 2;
            OnMusicOpened();
            if (play && fadeIn) { _mFade = 0; _mFadeTarget = 1; _mFadeDur = 1.5; _mPauseAtZero = false; }
            ApplyMusicVolume();
            SetMusicPlaying(play);
        }

        private void SetMusicPlaying(bool p)
        {
            _mPlaying = p;
            if (p) _mPlayer.Play(); else _mPlayer.Pause();
            string icon = p ? "❚❚" : "▶";
            MiniPlay.Content = icon;
            MusicPlayBtn.Content = icon;
            UpdateAwake();
        }

        private void MusicPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_mcur == null)
            {
                var it = MusicList.SelectedItem as PlayItem ?? _music.FirstOrDefault();
                if (it != null) PlayMusic(it, true, true);
                return;
            }
            if (_mPlaying) { SetMusicPlaying(false); return; }
            if (_mFade < 1) { _mFadeTarget = 1; _mFadeDur = 1; _mPauseAtZero = false; }
            SetMusicPlaying(true);
        }

        private void MusicStop_Click(object sender, RoutedEventArgs e)
        {
            if (_mcur == null) return;
            SetMusicPlaying(false);
            _mPlayer.Position = 0;
            UpdateMusicTime();
        }

        private void MusicNext_Click(object sender, RoutedEventArgs e) => MusicStep(1, true);
        private void MusicPrev_Click(object sender, RoutedEventArgs e) => MusicStep(-1, true);

        private void MusicStep(int dir, bool wrap)
        {
            if (_music.Count == 0) return;
            if (dir < 0 && _mOpened && _mPlayer.Position > 3) { _mPlayer.Position = 0; return; }
            int cur = _mcur == null ? -1 : _music.IndexOf(_mcur);
            int next;
            if (Settings.MusicShuffle && _music.Count > 1)
            {
                do next = _rnd.Next(_music.Count); while (next == cur);
            }
            else
            {
                next = cur + dir;
                if (next < 0 || next >= _music.Count)
                {
                    if (!wrap) { SetMusicPlaying(false); return; }
                    next = (next + _music.Count) % _music.Count;
                }
            }
            PlayMusic(_music[next], _mPlaying, false);
        }

        private void OnMusicOpened()
        {
            _mOpened = true;
            _mFails = 0;
            double dur = _mPlayer.Duration;
            _mSet = true;
            MPos.Maximum = Math.Max(dur, 0.1);
            MPos.Value = 0;
            _mSet = false;
            MTot.Text = Fmt(dur);
            MCur.Text = "00:00";
        }

        private void OnMusicEnded()
        {
            // один трек по кругу зациклен самим плеером (без пауз), сюда попадаем только для списка
            MusicStep(1, Settings.MusicLoop == 1);
        }

        private void OnMusicFailed()
        {
            MiniTitle.Text = "♪ Не удалось открыть: " + (_mcur?.Name ?? "");
            if (_mPlaying && ++_mFails < _music.Count) MusicStep(1, true);
            else SetMusicPlaying(false);
        }

        // ---------- Громкость, затухание, приглушение ----------

        private void ApplyMusicVolume()
        {
            double v = MusicVol.Value;
            _mPlayer.Volume = Math.Max(0, Math.Min(1, v * v * _mGain * _mFade)); // квадрат — ползунок ощущается естественнее
        }

        private void MusicTick()
        {
            // приглушаем музыку, пока идёт видео со звуком
            double target = 1;
            var a = _act;
            if (Settings.Duck && a?.Item != null && !a.IsImage && _playing && MuteChk.IsChecked != true)
            {
                try { if (a.Player.HasAudio) target = 0.25; } catch { }
            }
            if (_sfxActive > 0 && AudioPlayer.LiveCount > 0) target = Math.Min(target, 0.3); // под фанфару музыка тише
            else _sfxActive = 0;
            _mGain += (target - _mGain) * 0.12;

            if (_mFade != _mFadeTarget)
            {
                double step = 0.05 / Math.Max(0.1, _mFadeDur);
                _mFade = _mFade < _mFadeTarget ? Math.Min(_mFadeTarget, _mFade + step) : Math.Max(_mFadeTarget, _mFade - step);
                if (_mFade <= 0 && _mPauseAtZero)
                {
                    _mPauseAtZero = false;
                    SetMusicPlaying(false);
                }
            }
            ApplyMusicVolume();
            if (++_mTick % 5 == 0) UpdateMusicTime();
        }

        private void MusicFadeOut_Click(object sender, RoutedEventArgs e)
        {
            if (!_mPlaying) return;
            _mFadeTarget = 0;
            _mFadeDur = 3;
            _mPauseAtZero = true;
        }

        private void MusicFadeIn_Click(object sender, RoutedEventArgs e)
        {
            if (_mcur == null) { MusicPlayPause_Click(null, null); return; }
            _mFadeTarget = 1;
            _mFadeDur = 3;
            _mPauseAtZero = false;
            if (!_mPlaying) SetMusicPlaying(true);
        }

        private void MusicVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_mReady) return;
            Settings.MusicVol = MusicVol.Value;
            ApplyMusicVolume();
        }

        private void MusicLoop_Click(object sender, RoutedEventArgs e)
        {
            Settings.MusicLoop = (Settings.MusicLoop + 1) % LoopNames.Length;
            MusicLoopBtn.Content = LoopNames[Settings.MusicLoop];
            _mPlayer.Loop = Settings.MusicLoop == 2;
        }

        private void MusicShuffle_Click(object sender, RoutedEventArgs e) => Settings.MusicShuffle = MusicShuffleChk.IsChecked == true;
        private void Duck_Click(object sender, RoutedEventArgs e) => Settings.Duck = DuckChk.IsChecked == true;

        // ---------- Время ----------

        private void UpdateMusicTime()
        {
            if (!_mOpened) return;
            double p = _mPlayer.Position;
            MCur.Text = Fmt(p);
            if (!MPos.IsMouseCaptureWithin)
            {
                _mSet = true;
                MPos.Value = Math.Min(p, MPos.Maximum);
                _mSet = false;
            }
        }

        private void MPos_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mSet || !_mReady || !_mOpened) return;
            _mPlayer.Position = MPos.Value;
            MCur.Text = Fmt(MPos.Value);
        }

        // ---------- Включить музыку из программы (действие элемента) ----------

        private void MusicStartAction()
        {
            if (_mcur == null)
            {
                var it = MusicList.SelectedItem as PlayItem ?? _music.FirstOrDefault();
                if (it != null) PlayMusic(it, true, true);
                return;
            }
            if (!_mPlayer.IsPlaying)
            {
                _mFade = 0; _mFadeTarget = 1; _mFadeDur = 2; _mPauseAtZero = false;
                SetMusicPlaying(true);
            }
            else { _mFadeTarget = 1; _mPauseAtZero = false; }
        }

        // ---------- Звуковые эффекты и фанфары ----------

        private int DeviceNumber => _devIdx; // -1 — по умолчанию, иначе номер WaveOut

        public void PlaySfx(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            _sfxActive++;
            AudioPlayer.PlayOnce(path, Settings.SfxVol * Settings.SfxVol, DeviceNumber);
        }

        private void SfxVol_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mReady) Settings.SfxVol = SfxVol.Value;
        }

        private void BuildPads()
        {
            PadGrid.Children.Clear();
            for (int i = 0; i < Settings.Pads.Length; i++)
            {
                int k = i;
                var b = new Button { Padding = new Thickness(4, 8, 4, 8), Margin = new Thickness(2), FontSize = 12 };
                b.Content = PadLabel(k);
                b.ToolTip = "Клавиша " + (k + 1) + ". Левая кнопка — играть, правая — назначить звук";
                b.Click += (s, e) => PadFire(k);
                b.MouseRightButtonUp += (s, e) => { PadAssign(k); e.Handled = true; };
                PadGrid.Children.Add(b);
            }
        }

        private string PadLabel(int k)
        {
            var p = Settings.Pads[k];
            return (k + 1) + "  " + (string.IsNullOrEmpty(p) ? "＋ звук" : Path.GetFileNameWithoutExtension(p));
        }

        private void PadFire(int k)
        {
            if (string.IsNullOrEmpty(Settings.Pads[k])) { PadAssign(k); return; }
            PlaySfx(Settings.Pads[k]);
        }

        private void PadAssign(int k)
        {
            var dlg = new OpenFileDialog { Filter = AudioFilter, Title = "Звук для кнопки " + (k + 1) };
            if (dlg.ShowDialog() != true) return;
            Settings.Pads[k] = dlg.FileName;
            ((Button)PadGrid.Children[k]).Content = PadLabel(k);
        }

        private void PadsStop_Click(object sender, RoutedEventArgs e) => AudioPlayer.StopAllOnce();

        private void PadsClear_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < Settings.Pads.Length; i++) Settings.Pads[i] = null;
            BuildPads();
        }

        // ---------- Устройство вывода музыки и эффектов ----------

        private void RefreshDevices()
        {
            _devices = AudioPlayer.DeviceNames();
            int idx = -1;
            if (!string.IsNullOrEmpty(Settings.AudioDevice))
                idx = _devices.FindIndex(d => d == Settings.AudioDevice);
            _devIdx = idx;
            _mPlayer.Device = _devIdx;
            UpdateDeviceUi();
        }

        private void UpdateDeviceUi() =>
            DeviceBtn.Content = "🔈 " + (_devIdx < 0 ? "Устройство по умолчанию" : _devices[_devIdx]);

        private void Device_Click(object sender, RoutedEventArgs e)
        {
            _devices = AudioPlayer.DeviceNames(); // список мог измениться: подключили колонки/HDMI
            _devIdx++;
            if (_devIdx >= _devices.Count) _devIdx = -1;
            Settings.AudioDevice = _devIdx < 0 ? "" : _devices[_devIdx];
            _mPlayer.Device = _devIdx;
            UpdateDeviceUi();
        }

        private void WinSound_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true }); }
            catch { try { Process.Start("sndvol.exe"); } catch { } }
        }
    }
}
