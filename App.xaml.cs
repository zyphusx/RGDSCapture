using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using RGDSCapture.Core;
using RGDSCapture.Services;
using RGDSCapture.ViewModels;
using RGDSCapture.Views;

namespace RGDSCapture
{
    /// <summary>
    /// Composition root: loads settings, applies the saved theme, builds the
    /// view-model graph and shows the main window. Also installs last-resort
    /// exception logging so unexpected errors land in the event log and a
    /// crash file instead of silently killing the app.
    /// </summary>
    public partial class App : Application
    {
        private MainViewModel? _vm;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppPaths.EnsureCreated();

            var settingsService = new SettingsService();
            var settings = settingsService.Load();
            ThemeService.Apply(settings.ThemeValue);

            _vm = new MainViewModel(settingsService);

            DispatcherUnhandledException += (_, args) =>
            {
                ReportCrash(args.Exception);
                args.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                ReportCrash(args.Exception.InnerException ?? args.Exception);
                args.SetObserved();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (args.ExceptionObject is Exception ex) WriteCrashFile(ex);
            };

            var window = new MainWindow(_vm);
            MainWindow = window;
            window.Show();
        }

        private void ReportCrash(Exception ex)
        {
            WriteCrashFile(ex);
            _vm?.Log.Append($"[ERROR] {ex.Message}", isError: true);
        }

        private static void WriteCrashFile(Exception ex)
        {
            try
            {
                File.AppendAllText(AppPaths.CrashLogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch
            {
                // Nothing sane left to do if even crash logging fails.
            }
        }
    }
}
