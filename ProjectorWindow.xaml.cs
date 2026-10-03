using System.Windows;
using System.Windows.Media;

namespace VidShow
{
    public partial class ProjectorWindow : Window
    {
        public ProjectorWindow(MediaPlayer player)
        {
            InitializeComponent();
            VideoRect.Fill = MainWindow.MakeBrush(player);
        }

        // Показать окно во весь экран на заданном мониторе (границы в пикселях).
        public void ShowOn(System.Drawing.Rectangle bounds, double dpiScale)
        {
            WindowState = WindowState.Normal;
            Left = (bounds.Left + 20) / dpiScale;
            Top = (bounds.Top + 20) / dpiScale;
            Width = 400;
            Height = 300;
            if (!IsVisible) Show();
            WindowState = WindowState.Maximized;
        }
    }
}
