using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace VidShow
{
    // Элемент программы (cue): видео, картинка или чёрный экран + всё, что с ним должно происходить.
    public class PlayItem : INotifyPropertyChanged
    {
        public const string BlackPath = "::black";

        private static readonly HashSet<string> ImgExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
        private static readonly HashSet<string> AudioExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".opus" };

        private bool _playing, _hold;
        private string _title;

        public string Path { get; set; }
        public string Title
        {
            get => _title;
            set { _title = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); Refresh(); }
        }
        public string Name => _title ?? (IsBlack ? "⬛ Чёрный экран" : System.IO.Path.GetFileName(Path));

        public bool IsBlack => Path == BlackPath;
        public bool IsVirtual { get; set; } // служебный чёрный экран «затемнение в конце» — в очереди его нет
        public bool IsAudio => IsAudioPath(Path);
        public static bool IsAudioPath(string p) => AudioExt.Contains(System.IO.Path.GetExtension(p ?? ""));
        // «Статичный» элемент: картинка или чёрный экран — у него свои часы вместо видеоплеера
        public bool IsImage => IsBlack || ImgExt.Contains(System.IO.Path.GetExtension(Path ?? ""));

        // Ждать оператора: картинка/чёрный висит, пока не нажмут «дальше»; видео после конца остаётся на последнем кадре
        public bool Hold { get => _hold; set { _hold = value; Refresh(); } }

        public double In { get; set; }          // с какой секунды начинать (видео)
        public double Out { get; set; }         // до какой секунды играть (0 — до конца)
        public int Loops { get; set; } = 1;     // сколько раз сыграть (0 — по кругу, пока оператор не нажмёт «дальше»)
        public double Fade { get; set; } = -1;  // переход В этот элемент, с (-1 — общая настройка)
        public bool ViaBlack { get; set; }      // переход через чёрное, а не наплывом
        public bool EndBlack { get; set; }      // в конце плавно уйти в чёрный экран и ждать
        public string Sfx { get; set; }         // звук-фанфара, играет в момент старта элемента
        public int MusicAct { get; set; }       // 0 — музыка не трогаем, 1 — включить, 2 — плавно выключить
        public double Secs { get; set; }        // сколько держать картинку (0 — общая настройка)
        public double Duration { get; set; }    // длительность файла (определяется автоматически)

        public bool IsPlaying
        {
            get => _playing;
            set { _playing = value; Notify(nameof(IsPlaying)); }
        }

        // Длительность одного прохода с учётом обрезки, с
        public double OnePass
        {
            get
            {
                if (IsImage || Duration <= 0) return 0;
                double end = Out > 0 && Out < Duration ? Out : Duration;
                return Math.Max(0, end - In);
            }
        }

        public string Badge
        {
            get
            {
                var b = new StringBuilder();
                if (IsImage) b.Append(Hold ? "∞" : "🖼"); else if (Hold) b.Append("⏸");
                if (!IsImage && Loops != 1) b.Append(Loops == 0 ? "🔁∞" : "🔁" + Loops);
                if (!IsImage && (In > 0 || Out > 0)) b.Append("✂");
                if (ViaBlack || EndBlack) b.Append("◐");
                if (!string.IsNullOrEmpty(Sfx)) b.Append("🎺");
                if (MusicAct == 1) b.Append("♪"); else if (MusicAct == 2) b.Append("♪↓");
                return b.ToString();
            }
        }

        public string DurText => OnePass > 0 ? FmtTime(OnePass) : "";

        public void Refresh()
        {
            Notify(nameof(Name)); Notify(nameof(Badge)); Notify(nameof(DurText)); Notify(nameof(Hold));
        }

        public static string FmtTime(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        public static PlayItem Create(string path, bool? hold = null)
        {
            var it = new PlayItem { Path = path };
            it._hold = hold ?? it.IsImage;
            return it;
        }

        public static PlayItem CreateBlack() => new PlayItem { Path = BlackPath, _hold = true };

        // ---------- сохранение ----------

        private static string Inv(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        private static double ParseD(string s, double def) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        private static string Esc(string s) => (s ?? "").Replace("|", "¦").Replace("\r", " ").Replace("\n", " ");

        public string ToLine() => string.Join("|", new[]
        {
            "v2", Hold ? "1" : "0", Inv(In), Inv(Out), Loops.ToString(CultureInfo.InvariantCulture), Inv(Fade),
            ViaBlack ? "1" : "0", EndBlack ? "1" : "0", MusicAct.ToString(CultureInfo.InvariantCulture), Inv(Secs),
            Esc(Sfx), Esc(_title), Path
        });

        public static PlayItem FromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            if (line.StartsWith("v2|"))
            {
                var p = line.Split(new[] { '|' }, 13);
                if (p.Length < 13 || p[12].Length == 0) return null;
                var it = new PlayItem { Path = p[12] };
                it._hold = p[1] == "1";
                it.In = ParseD(p[2], 0);
                it.Out = ParseD(p[3], 0);
                it.Loops = (int)ParseD(p[4], 1);
                it.Fade = ParseD(p[5], -1);
                it.ViaBlack = p[6] == "1";
                it.EndBlack = p[7] == "1";
                it.MusicAct = (int)ParseD(p[8], 0);
                it.Secs = ParseD(p[9], 0);
                it.Sfx = p[10].Length > 0 ? p[10] : null;
                it._title = p[11].Length > 0 ? p[11] : null;
                return it;
            }
            // старый формат: H|путь / T|путь
            if (line.Length > 2 && line[1] == '|') return Create(line.Substring(2), line[0] == 'H');
            return null;
        }

        private void Notify(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
