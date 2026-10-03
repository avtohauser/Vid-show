using System;
using System.Linq;
using System.Windows;

namespace VidShow
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Для совсем слабых машин / проблемных драйверов: VidShow.exe --software
            if (e.Args.Any(a => a.Equals("--software", StringComparison.OrdinalIgnoreCase)))
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            base.OnStartup(e);
        }
    }
}
