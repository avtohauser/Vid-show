using System;
using System.Collections.ObjectModel;
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

        private readonly System.Windows.Media.MediaPlayer _mp = new System.Windows.Media.MediaPlayer();
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

            _mp.MediaOpened += (s, e) => OnMusicOpened();
            _mp.MediaEnded += (s, e) => OnMusicEnded();
            _mp.MediaFailed += (s, e) => OnMusicFailed();
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
            _mp.Close();
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
            _mp.Open(new Uri(it.Path));
            if (play && fadeIn) { _mFade = 0; _mFadeTarget = 1; _mFadeDur = 1.5; _mPauseAtZero = false; }
            ApplyMusicVolume();
            SetMusicPlaying(play);
        }

        private void SetMusicPlaying(bool p)
        {
            _mPlaying = p;
            if (p) _mp.Play(); else _mp.Pause();
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
            _mp.Position = TimeSpan.Zero;
            UpdateMusicTime();
        }

        private void MusicNext_Click(object sender, RoutedEventArgs e) => MusicStep(1, true);
        private void MusicPrev_Click(object sender, RoutedEventArgs e) => MusicStep(-1, true);

        private void MusicStep(int dir, bool wrap)
        {
            if (_music.Count == 0) return;
            if (dir < 0 && _mOpened && _mp.Position.TotalSeconds > 3) { _mp.Position = TimeSpan.Zero; return; }
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
            double dur = _mp.NaturalDuration.HasTimeSpan ? _mp.NaturalDuration.TimeSpan.TotalSeconds : 0;
            _mSet = true;
            MPos.Maximum = Math.Max(dur, 0.1);
            MPos.Value = 0;
            _mSet = false;
            MTot.Text = Fmt(dur);
            MCur.Text = "00:00";
        }

        private void OnMusicEnded()
        {
            if (Settings.MusicLoop == 2)
            {
                _mp.Position = TimeSpan.Zero;
                _mp.Play();
            }
            else MusicStep(1, Settings.MusicLoop == 1);
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
            _mp.Volume = Math.Max(0, Math.Min(1, v * v * _mGain * _mFade)); // квадрат — ползунок ощущается естественнее
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
        }

        private void MusicShuffle_Click(object sender, RoutedEventArgs e) => Settings.MusicShuffle = MusicShuffleChk.IsChecked == true;
        private void Duck_Click(object sender, RoutedEventArgs e) => Settings.Duck = DuckChk.IsChecked == true;

        // ---------- Время ----------

        private void UpdateMusicTime()
        {
            if (!_mOpened) return;
            double p = _mp.Position.TotalSeconds;
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
            _mp.Position = TimeSpan.FromSeconds(MPos.Value);
            MCur.Text = Fmt(MPos.Value);
        }
    }
}
