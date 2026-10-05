using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VidShow
{
    // Зацикливание без паузы: когда файл кончился — читаем сначала.
    internal sealed class LoopStream : WaveStream
    {
        private readonly WaveStream _src;
        public bool EnableLooping { get; set; }
        public LoopStream(WaveStream src) { _src = src; }
        public override WaveFormat WaveFormat => _src.WaveFormat;
        public override long Length => _src.Length;
        public override long Position { get => _src.Position; set => _src.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = _src.Read(buffer, offset + total, count - total);
                if (read == 0)
                {
                    if (!EnableLooping || _src.Position == 0) break;
                    _src.Position = 0;
                }
                else total += read;
            }
            return total;
        }
    }

    // Плеер музыки / эффектов на NAudio: выбор устройства вывода, громкость, бесшовный повтор.
    internal sealed class AudioPlayer : IDisposable
    {
        private WaveOutEvent _out;
        private MediaFoundationReader _reader;
        private LoopStream _loop;
        private VolumeSampleProvider _vol;
        private float _volume = 1;
        private bool _wantLoop, _playing, _disposed;
        private int _device = -1;

        public event Action Ended;
        public event Action Failed;

        public bool IsOpen => _reader != null;
        public bool IsPlaying => _playing;
        public double Duration => _reader != null ? _reader.TotalTime.TotalSeconds : 0;

        public double Position
        {
            get => _reader != null ? _reader.CurrentTime.TotalSeconds : 0;
            set
            {
                if (_reader == null) return;
                try { _reader.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, Math.Min(value, Duration))); } catch { }
            }
        }

        public bool Loop
        {
            get => _wantLoop;
            set { _wantLoop = value; if (_loop != null) _loop.EnableLooping = value; }
        }

        public double Volume
        {
            get => _volume;
            set { _volume = (float)Math.Max(0, Math.Min(1, value)); if (_vol != null) _vol.Volume = _volume; }
        }

        // -1 — устройство по умолчанию
        public int Device
        {
            get => _device;
            set { if (_device == value) return; _device = value; if (_reader != null) BuildOutput(_playing); }
        }

        public bool Open(string path)
        {
            CloseStream();
            try
            {
                _reader = new MediaFoundationReader(path);
                _loop = new LoopStream(_reader) { EnableLooping = _wantLoop };
                _vol = new VolumeSampleProvider(_loop.ToSampleProvider()) { Volume = _volume };
                BuildOutput(false);
                return true;
            }
            catch
            {
                CloseStream();
                return false;
            }
        }

        private void BuildOutput(bool resume)
        {
            DisposeOutput();
            try
            {
                _out = new WaveOutEvent { DeviceNumber = _device, DesiredLatency = 120 };
                _out.Init(new SampleToWaveProvider16(_vol));
                _out.PlaybackStopped += OnStopped;
                if (resume) _out.Play();
            }
            catch
            {
                _playing = false;
                DisposeOutput();
                Failed?.Invoke();
            }
        }

        private void OnStopped(object sender, StoppedEventArgs e)
        {
            if (_disposed) return;
            if (e.Exception != null) { _playing = false; Failed?.Invoke(); return; }
            if (_playing) { _playing = false; Ended?.Invoke(); }
        }

        public void Play() { if (_out == null) return; _playing = true; try { _out.Play(); } catch { _playing = false; Failed?.Invoke(); } }
        public void Pause() { if (_out == null) return; _playing = false; try { _out.Pause(); } catch { } }

        private void DisposeOutput()
        {
            if (_out == null) return;
            _out.PlaybackStopped -= OnStopped;
            try { _out.Stop(); } catch { }
            _out.Dispose();
            _out = null;
        }

        private void CloseStream()
        {
            _playing = false;
            DisposeOutput();
            _vol = null;
            _loop = null;
            if (_reader != null) { try { _reader.Dispose(); } catch { } _reader = null; }
        }

        public void Close() => CloseStream();

        public void Dispose()
        {
            _disposed = true;
            CloseStream();
        }

        // ---------- устройства ----------

        public static List<string> DeviceNames()
        {
            var res = new List<string>();
            try
            {
                for (int i = 0; i < WaveOut.DeviceCount; i++) res.Add(WaveOut.GetCapabilities(i).ProductName);
            }
            catch { }
            return res;
        }

        // Короткий звук (фанфара, аплодисменты): сам играет и сам освобождает ресурсы.
        private static readonly List<AudioPlayer> Live = new List<AudioPlayer>();
        public static int LiveCount => Live.Count;

        public static void PlayOnce(string path, double volume, int device)
        {
            var p = new AudioPlayer { Volume = volume, _device = device };
            Action done = () => { Live.Remove(p); p.Dispose(); };
            p.Ended += done;
            p.Failed += done;
            if (!p.Open(path)) { p.Dispose(); return; }
            Live.Add(p);
            p.Play();
        }

        public static void StopAllOnce()
        {
            foreach (var p in Live.ToArray()) { Live.Remove(p); p.Dispose(); }
        }
    }
}
