using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace VidShow
{
    // Окно свойств элемента программы: обрезка, повторы, переходы, фанфара, действие с музыкой.
    public partial class CueWindow : Window
    {
        private readonly PlayItem _it;
        private readonly Func<PlayItem, double?> _getPos;
        private string _sfx;
        private int _music;

        public CueWindow(PlayItem it, Func<PlayItem, double?> getPos)
        {
            InitializeComponent();
            _it = it;
            _getPos = getPos;

            FileLine.Text = it.IsBlack ? "Чёрный экран (пауза)" : it.Path;
            TitleBox.Text = it.Title ?? "";
            bool still = it.IsImage;
            TrimPanel.Visibility = still ? Visibility.Collapsed : Visibility.Visible;
            StillPanel.Visibility = still ? Visibility.Visible : Visibility.Collapsed;
            EndBlackChk.Visibility = still ? Visibility.Collapsed : Visibility.Visible;

            InBox.Text = it.In > 0 ? FmtT(it.In) : "";
            OutBox.Text = it.Out > 0 ? FmtT(it.Out) : "";
            LoopsBox.Text = it.Loops == 0 ? "" : it.Loops.ToString(CultureInfo.InvariantCulture);
            LoopInf.IsChecked = it.Loops == 0;
            LoopsBox.IsEnabled = it.Loops != 0;
            SecsBox.Text = it.Secs > 0 ? it.Secs.ToString("0.#", CultureInfo.InvariantCulture) : "";
            HoldChk.IsChecked = it.Hold;
            EndBlackChk.IsChecked = it.EndBlack;
            FadeBox.Text = it.Fade >= 0 ? it.Fade.ToString("0.#", CultureInfo.InvariantCulture) : "";
            ViaBlackChk.IsChecked = it.ViaBlack;
            _sfx = it.Sfx;
            SfxBox.Text = _sfx ?? "";
            SetMusic(it.MusicAct);

            bool playing = getPos(it) != null;
            InGet.IsEnabled = OutGet.IsEnabled = playing;
            if (!playing && !still)
                TrimNote.Text = "Чтобы взять время с плеера: включите этот элемент (двойной клик в очереди), перемотайте на нужное место, затем откройте свойства снова. Время можно ввести и вручную.";
        }

        private void Window_SourceInitialized(object sender, EventArgs e) =>
            Native.DarkTitleBar(new WindowInteropHelper(this).Handle);

        private static string FmtT(double s)
        {
            var t = TimeSpan.FromSeconds(s);
            string f = t.TotalHours >= 1 ? @"h\:mm\:ss\.f" : @"m\:ss\.f";
            return t.ToString(f, CultureInfo.InvariantCulture);
        }

        // 83 | 1:23 | 1:23.5 | 1:02:03
        private static bool TryParseT(string text, out double sec)
        {
            sec = 0;
            text = (text ?? "").Trim().Replace(',', '.');
            if (text.Length == 0) return true;
            var parts = text.Split(':');
            if (parts.Length > 3) return false;
            double mult = 1;
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
                sec += v * mult;
                mult *= 60;
            }
            return true;
        }

        private void InGet_Click(object sender, RoutedEventArgs e)
        {
            var p = _getPos(_it);
            if (p != null) InBox.Text = FmtT(p.Value);
        }

        private void OutGet_Click(object sender, RoutedEventArgs e)
        {
            var p = _getPos(_it);
            if (p != null) OutBox.Text = FmtT(p.Value);
        }

        private void LoopInf_Click(object sender, RoutedEventArgs e) => LoopsBox.IsEnabled = LoopInf.IsChecked != true;

        private void SfxPick_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Звук|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac;*.ogg;*.opus|Все файлы|*.*" };
            if (dlg.ShowDialog() == true) { _sfx = dlg.FileName; SfxBox.Text = _sfx; }
        }

        private void SfxClear_Click(object sender, RoutedEventArgs e) { _sfx = null; SfxBox.Text = ""; }

        private void SetMusic(int m)
        {
            _music = m;
            Mus0.IsChecked = m == 0;
            Mus1.IsChecked = m == 1;
            Mus2.IsChecked = m == 2;
        }

        private void Mus_Click(object sender, RoutedEventArgs e) =>
            SetMusic(sender == Mus1 ? 1 : sender == Mus2 ? 2 : 0);

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            double inS = 0, outS = 0, fade = -1, secs = 0;
            int loops = 1;
            if (!_it.IsImage)
            {
                if (!TryParseT(InBox.Text, out inS)) { ErrText.Text = "Не понимаю время в поле «Начать с»"; return; }
                if (!TryParseT(OutBox.Text, out outS)) { ErrText.Text = "Не понимаю время в поле «Снять на»"; return; }
                if (outS > 0 && outS <= inS) { ErrText.Text = "«Снять на» должно быть позже, чем «Начать с»"; return; }
                if (LoopInf.IsChecked == true) loops = 0;
                else if (LoopsBox.Text.Trim().Length > 0)
                {
                    if (!int.TryParse(LoopsBox.Text.Trim(), out loops) || loops < 1) { ErrText.Text = "Повторов должно быть целое число от 1"; return; }
                }
            }
            else if (SecsBox.Text.Trim().Length > 0 &&
                     (!double.TryParse(SecsBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out secs) || secs < 0))
            { ErrText.Text = "Секунды — число"; return; }
            if (FadeBox.Text.Trim().Length > 0 &&
                (!double.TryParse(FadeBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out fade) || fade < 0))
            { ErrText.Text = "Длительность перехода — число секунд"; return; }

            _it.Title = TitleBox.Text;
            _it.In = inS;
            _it.Out = outS;
            _it.Loops = loops;
            _it.Secs = secs;
            _it.Hold = HoldChk.IsChecked == true;
            _it.EndBlack = !_it.IsImage && EndBlackChk.IsChecked == true;
            _it.Fade = fade;
            _it.ViaBlack = ViaBlackChk.IsChecked == true;
            _it.Sfx = _sfx;
            _it.MusicAct = _music;
            _it.Refresh();
            DialogResult = true;
        }
    }
}
