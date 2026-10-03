using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VidShow
{
    // Раскадровка для таймлайна: отдельный «тихий» MediaPlayer по очереди прыгает по видео
    // и снимает N кадров. Работает в фоне, результат кэшируется на время сессии.
    internal sealed class Filmstrip
    {
        public const int Count = 16;
        private const int W = 160, H = 90;
        private static readonly Dictionary<string, BitmapSource[]> Cache = new Dictionary<string, BitmapSource[]>();

        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        private MediaPlayer _p;
        private string _path;
        private double _dur;
        private int _i;
        private bool _pending, _opened;
        private BitmapSource[] _frames;
        private Action<int, BitmapSource> _onTile;

        public Filmstrip() { _timer.Tick += (s, e) => Step(); }

        public string Path => _path;

        public void Start(string path, double durationSec, Action<int, BitmapSource> onTile)
        {
            if (path == _path && _p != null) return; // уже идёт/готово
            Cancel();
            if (durationSec <= 0) return;
            if (Cache.TryGetValue(path, out var cached))
            {
                _path = path;
                for (int k = 0; k < cached.Length; k++) onTile(k, cached[k]);
                return;
            }
            _path = path;
            _dur = durationSec;
            _onTile = onTile;
            _frames = new BitmapSource[Count];
            _i = 0;
            _pending = false;
            _opened = false;
            _p = new MediaPlayer { ScrubbingEnabled = true, Volume = 0, IsMuted = true };
            _p.MediaOpened += (s, e) =>
            {
                _opened = true;
                _p.Play();
                _p.Pause();
                _timer.Interval = TimeSpan.FromMilliseconds(350); // даём декодеру «прогреться»
                _timer.Start();
            };
            _p.MediaFailed += (s, e) => Cancel();
            _p.Open(new Uri(path));
        }

        public void Cancel()
        {
            _timer.Stop();
            _timer.Interval = TimeSpan.FromMilliseconds(140);
            if (_p != null) { try { _p.Close(); } catch { } _p = null; }
            _path = null;
            _onTile = null;
        }

        private void Step()
        {
            if (_p == null || !_opened) return;
            _timer.Interval = TimeSpan.FromMilliseconds(140);
            if (_pending)
            {
                var bmp = Grab();
                if (bmp != null)
                {
                    _frames[_i] = bmp;
                    _onTile?.Invoke(_i, bmp);
                }
                _i++;
                _pending = false;
                if (_i >= Count)
                {
                    if (Cache.Count > 12) Cache.Clear();
                    Cache[_path] = _frames;
                    var keep = _path;
                    _timer.Stop();
                    try { _p.Close(); } catch { }
                    _p = null;
                    _path = keep; // помечаем как готовое: повторный Start с тем же путём не нужен
                    return;
                }
            }
            _p.Position = TimeSpan.FromSeconds((_i + 0.5) / Count * _dur);
            _pending = true;
        }

        private BitmapSource Grab()
        {
            try
            {
                double vw = _p.NaturalVideoWidth, vh = _p.NaturalVideoHeight;
                if (vw <= 0 || vh <= 0) { vw = 16; vh = 9; }
                double k = Math.Max(W / vw, H / vh);
                double rw = vw * k, rh = vh * k;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));
                    dc.DrawVideo(_p, new Rect((W - rw) / 2, (H - rh) / 2, rw, rh));
                    dc.Pop();
                }
                var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }
    }
}
