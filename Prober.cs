using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace VidShow
{
    // Фоном определяет длительность видео в очереди (нужна для плана мероприятия и подписей).
    internal sealed class Prober
    {
        private readonly Queue<PlayItem> _queue = new Queue<PlayItem>();
        private readonly DispatcherTimer _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly Action _changed;
        private MediaPlayer _p;
        private PlayItem _cur;

        public Prober(Action changed)
        {
            _changed = changed;
            _timeout.Tick += (s, e) => Finish(-1);
        }

        public void Enqueue(IEnumerable<PlayItem> items)
        {
            foreach (var it in items)
                if (!it.IsImage && it.Duration == 0) _queue.Enqueue(it);
            Next();
        }

        private void Next()
        {
            if (_cur != null) return;
            while (_queue.Count > 0)
            {
                var it = _queue.Dequeue();
                if (it.Duration != 0 || !File.Exists(it.Path)) continue;
                _cur = it;
                _p = new MediaPlayer { Volume = 0, IsMuted = true };
                _p.MediaOpened += (s, e) =>
                    Finish(_p != null && _p.NaturalDuration.HasTimeSpan ? _p.NaturalDuration.TimeSpan.TotalSeconds : -1);
                _p.MediaFailed += (s, e) => Finish(-1);
                _timeout.Start();
                try { _p.Open(new Uri(it.Path)); } catch { Finish(-1); }
                return;
            }
        }

        private void Finish(double dur)
        {
            _timeout.Stop();
            if (_p != null) { try { _p.Close(); } catch { } _p = null; }
            var it = _cur;
            _cur = null;
            if (it != null)
            {
                it.Duration = dur > 0 ? dur : -1;
                it.Refresh();
                _changed?.Invoke();
            }
            Next();
        }
    }
}
