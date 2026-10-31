using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace EksaCompanion
{
    public partial class App : Application
    {
        private const string MutexName = "Local\\EksaE910Companion_SingleInstance_Mutex";
        private const string ShowEventName = "Local\\EksaE910Companion_ShowUI_Event";

        private static Mutex _singleInstanceMutex;
        private static EventWaitHandle _showUiEvent;
        private static Thread _showListenerThread;

        public App()
        {
            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        public static void Log(string msg)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.log");
                File.AppendAllText(path, string.Format("[{0:HH:mm:ss.fff}] {1}\r\n", DateTime.Now, msg));
            }
            catch { }
        }

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs args)
            {
                Log("AppDomain Unhandled Exception: " + args.ExceptionObject);
            };

            this.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs args)
            {
                Log("Dispatcher Unhandled Exception: " + args.Exception);
                args.Handled = true;
            };

            Log("Application_Startup started");

            bool createdNew;
            try
            {
                _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
            }
            catch (Exception ex)
            {
                Log("Mutex exception: " + ex.Message);
                createdNew = true;
            }

            if (!createdNew)
            {
                Log("Existing instance detected. Signalling to show UI...");
                try
                {
                    EventWaitHandle existingEvent;
                    if (EventWaitHandle.TryOpenExisting(ShowEventName, out existingEvent))
                    {
                        existingEvent.Set();
                        existingEvent.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Log("Event signal exception: " + ex.Message);
                }
                Environment.Exit(0);
                return;
            }

            // Create ShowUI IPC Event
            try
            {
                _showUiEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            }
            catch { }

            this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Log("Creating MainWindow...");
            MainWindow mainWindow = new MainWindow();
            this.MainWindow = mainWindow;

            // Start ShowUI listener thread
            if (_showUiEvent != null)
            {
                _showListenerThread = new Thread(delegate()
                {
                    while (true)
                    {
                        try
                        {
                            _showUiEvent.WaitOne();
                            Log("Received show UI signal!");
                            Dispatcher.Invoke(new Action(delegate()
                            {
                                mainWindow.ShowWindow();
                            }));
                        }
                        catch
                        {
                            break;
                        }
                    }
                });
                _showListenerThread.IsBackground = true;
                _showListenerThread.Name = "ShowUiListenerThread";
                _showListenerThread.Start();
            }

            bool startMinimized = true;
            AppSettings settings = AppSettings.Load();
            if (e.Args.Length > 0 && e.Args[0].Equals("--show", StringComparison.OrdinalIgnoreCase))
            {
                startMinimized = false;
            }
            else if (!settings.StartMinimized && (e.Args.Length == 0 || !e.Args[0].Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                startMinimized = false;
            }

            Log(string.Format("startMinimized = {0}", startMinimized));
            if (!startMinimized)
            {
                mainWindow.ShowWindow();
            }
            Log("Application_Startup finished. ShutdownMode=" + this.ShutdownMode);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("Application OnExit");
            if (_showUiEvent != null)
            {
                try { _showUiEvent.Dispose(); } catch { }
            }
            if (_singleInstanceMutex != null)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                    _singleInstanceMutex.Dispose();
                }
                catch { }
            }
            base.OnExit(e);
        }
    }
}
