using System;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FarArc.Service;
using FarArc.Utils.Tracing;
using FarArc.Utils;


namespace FarArc
{
    static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            var argss = args.ToList();
            AppInitHelper.Init();
            AppStartupHelper.Init(argss); // in this method, it will call Environment.Exit() if needed
            var application = new App();
            application.InitializeComponent();
            application.Run();
        }
    }

    public partial class App : Application
    {
        public static ResourceDictionary? ResourceDictionary { get; private set; } = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            ResourceDictionary = this.Resources;
            base.OnStartup(e);

            // First, make a sound (one second of silence) in the main window
            // so that the Volume Mixer and others will recognize FarArc as
            // an application that outputs sound.
            //
            // Otherwise, FarArc is only be detected as a sound application
            // when an RDP session is started. However, it seemed odd that it
            // remained in this state even after all RDP sessions were
            // terminated.
            //
            // So while this application is running, from start to finish,
            // it's better to be visible as a sound application in the Volume
            // Mixer and others.
            try
            {
                var sri = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/dummy.wav"));
                if (sri != null)
                {
                    using var s = sri.Stream;
                    System.Media.SoundPlayer player = new System.Media.SoundPlayer(s);
                    player.Load();
                    player.Play();
                }
            }
            catch (Exception)
            {
                // ignored
            }
        }

        public static bool ExitingFlag = false;
        public static void Close(int exitCode = 0)
        {
            // workaround
            Task.Factory.StartNew(() =>
            {
                Thread.Sleep(5 * 1000);
                Environment.Exit(1);
            });
            ExitingFlag = true;
            Application.Current.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown(exitCode);
            });
        }
    }
}
