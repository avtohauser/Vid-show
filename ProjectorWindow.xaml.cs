using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VidShow
{
    // Окно проектора: только видео. Полноэкранное (рамки нет, курсор скрыт) на выбранном мониторе;
    // если монитор один — обычное окно, чтобы не закрыть пульт.
    public partial class ProjectorWindow : Window
    {
        private bool _allowClose;

        public ProjectorWindow()
        {
            InitializeComponent();
        }

        // Два слоя (A/B): пока один играет, второй готов и проявляется поверх него.
        public void SetLayer(int index, Brush brush, double opacity, int z)
        {
            var r = index == 0 ? RectA : RectB;
            r.Fill = brush;
            r.Opacity = opacity;
            Panel.SetZIndex(r, z);
        }

        public void SetOpacity(int index, double opacity) => (index == 0 ? RectA : RectB).Opacity = opacity;

        public void ShowFullscreen(System.Drawing.Rectangle bounds, double dpiScale)
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.None;
            Place(bounds, dpiScale, 400, 300);
            WindowState = WindowState.Maximized;
        }

        public void ShowWindowed(System.Drawing.Rectangle bounds, double dpiScale)
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Topmost = false;
            ShowInTaskbar = true;
            Cursor = Cursors.Arrow;
            Place(bounds, dpiScale, 640, 360);
        }

        private void Place(System.Drawing.Rectangle bounds, double dpiScale, double w, double h)
        {
            Left = (bounds.Left + 40) / dpiScale;
            Top = (bounds.Top + 40) / dpiScale;
            Width = w;
            Height = h;
            if (!IsVisible) Show();
        }

        public void SetBlackout(bool on, bool animate = true)
        {
            double to = on ? 0 : 1;
            if (!animate) { Stage.BeginAnimation(OpacityProperty, null); Stage.Opacity = to; return; }
            Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(350)));
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Hide();
        }

        public void ForceClose()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose) { e.Cancel = true; Hide(); }
            base.OnClosing(e);
        }
    }
}
