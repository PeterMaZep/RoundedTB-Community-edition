using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Serilog;
using WpfApplication = System.Windows.Application;

namespace RoundedTB
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static readonly string MutexName = "RoundedTB_SingleInstance_Mutex";
        private Mutex _mutex;
        private bool _allowShutdown = false;
        private Window _hiddenWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Logging first so we can actually record why a second-instance launch aborts.
            SetupLogging();
            SetupExceptionHandling();

            // Two-layer single-instance check:
            //   1. Named mutex — fast path for the common case (classic+classic, MSIX+MSIX).
            //   2. Process enumeration — catches edge cases where the mutex is namespace-
            //      isolated (different integrity level, cross-container scenarios). Both
            //      classic and MSIX builds launch the same RoundedTB.exe, so ProcessName
            //      matches in either combination.
            _mutex = new Mutex(true, MutexName, out bool isNewInstance);
            if (!isNewInstance || AnotherInstanceRunning())
            {
                Log.Warning("RoundedTB is already running (mutexHeld={Mutex}); aborting second instance.", !isNewInstance);
                MessageBox.Show(
                    "RoundedTB is already running.\n\nUse the tray icon to access the existing instance. If you can't see the tray icon, check Task Manager for 'RoundedTB' and end it before launching again.",
                    "RoundedTB",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                _allowShutdown = true;
                Shutdown();
                return;
            }

            Log.Information("RoundedTB Started");

            base.OnStartup(e);

            // Prevent automatic shutdown when main window closes
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Create a hidden window to keep the application alive
            _hiddenWindow = new Window()
            {
                Title = "RoundedTB Hidden",
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                Visibility = Visibility.Hidden
            };
            _hiddenWindow.Show();

            // Log dispatcher shutdown for diagnostics. We used to throw here to
            // "prevent" unwanted shutdown, but that just left the dispatcher in
            // a zombie state (MainWindow disposed, process alive). WM_CLOSE is
            // now intercepted in MainWindow.CloseButtonHook so X-button clicks
            // never reach this path.
            this.Dispatcher.ShutdownStarted += (sender, args) =>
            {
                if (!_allowShutdown)
                {
                    Log.Warning("ShutdownStarted with _allowShutdown=false - something other than CloseMenuItem triggered shutdown.");
                }
                else
                {
                    Log.Information("ShutdownStarted (_allowShutdown=true)");
                }
            };

        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (!_allowShutdown)
            {
                // Previously we suppressed OnExit to "keep the app alive" after
                // WPF tried to shut down on X-button clicks. That created a
                // zombie process (MainWindow disposed, process alive, tray Show
                // broken). WM_CLOSE is now intercepted upstream, so reaching
                // this branch means something actually needs to shut us down;
                // log it and let the exit proceed normally.
                Log.Warning("OnExit called with _allowShutdown=false - shutting down anyway to avoid zombie state.");
            }

            Log.Information("App.OnExit called - Application is shutting down");
            Log.Information("RoundedTB Exiting");
            Log.CloseAndFlush();

            // Release the mutex. ReleaseMutex throws if the current thread
            // doesn't own it (true for second-instance exits where we never
            // acquired). Swallow that case; Dispose always closes the handle.
            try { _mutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
            _mutex?.Dispose();

            base.OnExit(e);
        }

        protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            Log.Information("Session ending - allowing shutdown");
            base.OnSessionEnding(e);
        }

        public new void Shutdown()
        {
            Log.Information("App.Shutdown() called - allowing shutdown");
            _allowShutdown = true;
            base.Shutdown();
        }

        public new void Shutdown(int exitCode)
        {
            Log.Information($"App.Shutdown({exitCode}) called - allowing shutdown");
            _allowShutdown = true;
            base.Shutdown(exitCode);
        }

        // Enumerate processes with the same image name as ours. Both classic and
        // MSIX builds launch RoundedTB.exe, so ProcessName matches either way.
        // This backstops the named mutex check for cases where kernel namespace
        // isolation (different integrity, some MSIX containers) hides the mutex
        // from us even though another instance is very much running.
        private static bool AnotherInstanceRunning()
        {
            using var me = Process.GetCurrentProcess();
            var siblings = Process.GetProcessesByName(me.ProcessName);
            try
            {
                return siblings.Any(p => p.Id != me.Id);
            }
            finally
            {
                foreach (var p in siblings) p.Dispose();
            }
        }

        private void SetupLogging()
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoundedTB", "logs");
                Directory.CreateDirectory(logDir);

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Debug()
                    .WriteTo.File(path: Path.Combine(logDir, "log-.txt"),
                                  rollingInterval: RollingInterval.Day,
                                  retainedFileCountLimit: 7,
                                  outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();
            }
            catch (Exception ex)
            {
                // Fallback: If logging setup fails, we can't log it to file, but we can write to debug/console
                System.Diagnostics.Debug.WriteLine($"Failed to setup logging: {ex}");
            }
        }

        private void SetupExceptionHandling()
        {
            // UI Thread Exceptions
            this.DispatcherUnhandledException += (sender, args) =>
            {
                Log.Fatal(args.Exception, "Unhandled UI Exception");
                // We are not setting Handled = true, treating it as fatal for now, but at least we log it.
            };

            // Background Task Exceptions
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                Log.Error(args.Exception, "Unobserved Task Exception");
                args.SetObserved();
            };

            // Catch-all for AppDomain
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                Log.Fatal(ex, "AppDomain Unhandled Exception. IsTerminating: {IsTerminating}", args.IsTerminating);
                Log.CloseAndFlush();
            };
        }
    }
}
