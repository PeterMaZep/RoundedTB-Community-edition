using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Reflection;
using System.Windows.Threading;
using System.Windows.Interop;
using DesktopBridge;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using System.Diagnostics;
using Microsoft.Win32;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;
using Hardcodet.Wpf.TaskbarNotification;
using System.Linq;
using System.Windows.Controls;

namespace RoundedTB
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    ///
    /// Many thanks to
    ///  - FloatingMilkshake
    ///  - cardin
    ///  - cleverActon0126
    ///  for your gracious donations! 💖
    ///
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string StartupShortcutName = "RoundedTB.lnk";
        private const string StartupRunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRunValueName = "RoundedTB";

        public bool isWindows11;
        public List<Types.Taskbar> taskbarDetails = new List<Types.Taskbar>();
        public bool shouldReallyDieNoReally = false;
        public string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtb.json");
        // Points at the Serilog rolling-file directory so the About window's
        // "log" button opens the actual logs. UWP packaging redirects
        // LocalApplicationData into the package container, so the same
        // expression resolves correctly in both classic and MSIX builds.
        public string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoundedTB", "logs");
        private TaskbarIcon _trayIcon;
        public Types.Settings activeSettings = new Types.Settings();
        public BackgroundWorker taskbarThread = new BackgroundWorker();
        public IntPtr hwndDesktopButton = IntPtr.Zero;
        public int lastDynDistance = 0;
        public int numberToForceRefresh = 0;
        public bool isCentred = false;
        public bool isAlreadyRunning = false;
        public Background background;
        public Interaction interaction;
        private HwndSource source;
        // Was an int tracking the legacy single AutoHide value. Now a nullable bool
        // tracking whether ANY segment in the active mode (simple vs dynamic) has
        // autohide enabled, since per-segment hides are coordinated by the background
        // worker but the AlwaysOnTop / work-area side-effects are still per-window.
        // null = not yet applied (skip the false→false noop on first launch).
        private bool? lastAppliedAnyAutoHide = null;
        public int selectedSegment = 0; // 0 = Simple, 1 = AppList, 2 = Tray, 3 = Widgets
        /// <summary>
        /// Versions:
        /// -1: Canary
        ///  0: R3.0
        ///  1: P3.1B
        ///  2: R3.1
        ///  3: R4
        /// </summary>
        public int version = -1;


        private VisiblityControlManager rectStands;

        record VisiblityControlSet(System.Windows.Controls.Button KeyControl, IList<System.Windows.Controls.Control> FollowingControls);

        record VisiblityControlManager(VisiblityControlSet CenterBar, VisiblityControlSet TaskTray, VisiblityControlSet Widgets, VisiblityControlSet Clock)
        {
            public IReadOnlyList<VisiblityControlSet> All => new[] { CenterBar, TaskTray, Widgets, Clock };
            public IReadOnlyList<VisiblityControlSet> AllWithoutCenterBar => new[] { TaskTray, Widgets, Clock };
            public VisiblityControlSet At(int no) => All[no - 1];

            internal void Focus(VisiblityControlSet set)
            {
                set.KeyControl.Style = set.KeyControl.TryFindResource("AccentButtonStyle") as Style;
                foreach (var c in set.FollowingControls)
                {
                    c.Visibility = Visibility.Visible;
                }

                All.Except(new[] { set }).ForEach(x =>
                {
                    x.KeyControl.ClearValue(System.Windows.Controls.Button.StyleProperty);
                    foreach (var c in x.FollowingControls)
                    {
                        c.Visibility = Visibility.Hidden;
                    }
                });
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            Icon = new BitmapImage(new Uri(Branding.IconResourcePath));

            rectStands = new(
                new VisiblityControlSet(taskbarRectStandIn, new[] { dynamicCheckBox }),
                new VisiblityControlSet(trayRectStandIn, new[] { showTrayCheckBox }),
                new VisiblityControlSet(widgetsRectStandIn, new Control[] { showWidgetsCheckBox, widgetWidthInput, widgetWidthLabel }),
                new VisiblityControlSet(clockRectStandIn, new Control[] { showClockCheckBox, clockWidthInput, clockWidthLabel })
            );


            // Check OS build, as behaviours rather-annoyingly differ between Windows 11 and Windows 10
            RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var buildNumber = registryKey.GetValue("CurrentBuild").ToString();
            if (Convert.ToInt32(buildNumber) >= 21996)
            {
                isWindows11 = true;
            }
            else
            {
                isWindows11 = false;
                activeSettings.IsWindows11 = false;
                dynamicCheckBox.Content = "Split mode";
                fillAltTabCheckBox.Content = "[Unavailable]";
            }

            // Initialise functions
            background = new Background();
            interaction = new Interaction();

            // Set the MainWindow reference directly to avoid lazy initialization issues
            background.SetMainWindow(this);
            interaction.SetMainWindow(this);

            // Note: Single instance detection is now handled in App.xaml.cs using Mutex
            SetupTrayIcon();

            if (IsRunningAsUWP())
            {
#pragma warning disable CS4014
                StartupInit(true);
                configPath = Path.Combine(Windows.Storage.ApplicationData.Current.RoamingFolder.Path, "rtb.json");
            }

            if (!IsRunningAsUWP())
            {
                RefreshStartupCheckboxFromSystem();
                if (StartupCheckBox.IsChecked == true)
                {
                    ShowMenuItem.Header = "Show RoundedTB";
                }
            }
            taskbarThread.WorkerSupportsCancellation = true;
            taskbarThread.WorkerReportsProgress = true;
            taskbarThread.DoWork += background.DoWork;

            // Load settings into memory/UI
            interaction.FileSystem();
            activeSettings = interaction.ReadJSON();

            if (isWindows11)
            {
                activeSettings.IsWindows11 = true;
            }
            else
            {
                activeSettings.IsWindows11 = false;
            }
            // Default settings
            if (activeSettings == null)
            {

                if (isWindows11) // Default settings for Windows 11
                {
                    activeSettings = new Types.Settings()
                    {
                        SimpleTaskbarLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                        DynamicAppListLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                        DynamicTrayLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                        DynamicWidgetsLayout = new Types.SegmentSettings { CornerRadius = 7, MarginLeft = 3, MarginTop = 3, MarginRight = 3, MarginBottom = 3 },
                        WidgetsWidth = 168,
                        ClockWidth = 110,
                        IsDynamic = false,
                        IsCentred = false,
                        IsWindows11 = true,
                        ShowTray = false,
                        ShowWidgets = false,
                        ShowSecondaryClock = false,
                        CompositionCompat = false,
                        IsNotFirstLaunch = false,
                        FillOnMaximise = true,
                        FillOnTaskSwitch = true,
                        ShowSegmentsOnHover = false,
                        AutoHide = 0,
                        HoverRevealDelayMs = 1000,
                        HoverHideDelayMs = 0
                    };
                }
                else // Default settings for Windows 10
                {
                    activeSettings = new Types.Settings()
                    {
                        SimpleTaskbarLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                        DynamicAppListLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                        DynamicTrayLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                        DynamicWidgetsLayout = new Types.SegmentSettings { CornerRadius = 16, MarginLeft = 2, MarginTop = 2, MarginRight = 2, MarginBottom = 2 },
                        WidgetsWidth = 168,
                        ClockWidth = 110,
                        IsDynamic = false,
                        IsCentred = false,
                        IsWindows11 = false,
                        ShowTray = false,
                        ShowWidgets = false,
                        ShowSecondaryClock = false,
                        CompositionCompat = false,
                        IsNotFirstLaunch = false,
                        FillOnMaximise = true,
                        FillOnTaskSwitch = false,
                        ShowSegmentsOnHover = false,
                        AutoHide = 0,
                        HoverRevealDelayMs = 1000,
                        HoverHideDelayMs = 0
                    };
                }
            }

            if (version != activeSettings.Version && version != -1)
            {
                activeSettings.IsNotFirstLaunch = false;
            }
            activeSettings.Version = version;


            // Checks if advanced margins are configured
            if (activeSettings.IsDynamic)
            {
                cornerRadiusInput.Text = activeSettings.DynamicAppListLayout.CornerRadius.ToString();
                cornerRadiusSlider.Value = activeSettings.DynamicAppListLayout.CornerRadius;
                mTopInput.Text = activeSettings.DynamicAppListLayout.MarginTop.ToString();
                mLeftInput.Text = activeSettings.DynamicAppListLayout.MarginLeft.ToString();
                mBottomInput.Text = activeSettings.DynamicAppListLayout.MarginBottom.ToString();
                mRightInput.Text = activeSettings.DynamicAppListLayout.MarginRight.ToString();

                selectedSegment = 1;
            }
            else
            {
                cornerRadiusInput.Text = activeSettings.SimpleTaskbarLayout.CornerRadius.ToString();
                cornerRadiusSlider.Value = activeSettings.SimpleTaskbarLayout.CornerRadius;
                mTopInput.Text = activeSettings.SimpleTaskbarLayout.MarginTop.ToString();
                mLeftInput.Text = activeSettings.SimpleTaskbarLayout.MarginLeft.ToString();
                mBottomInput.Text = activeSettings.SimpleTaskbarLayout.MarginBottom.ToString();
                mRightInput.Text = activeSettings.SimpleTaskbarLayout.MarginRight.ToString();

                selectedSegment = 0;
            }

            // Get whether or not taskbar is centred
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced"))
                {
                    if (key != null)
                    {
                        int val = (int)key.GetValue("TaskbarAl");
                        if (val == 1)
                        {
                            isCentred = true;
                        }
                        else
                        {
                            isCentred = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read taskbar alignment from registry");
            }
            if (!isWindows11)
            {
                activeSettings.IsCentred = false;
            }

            // Copy and apply settings to UI
            dynamicCheckBox.IsChecked = activeSettings.IsDynamic;
            centredCheckBox.IsChecked = activeSettings.IsCentred;
            showTrayCheckBox.IsChecked = activeSettings.ShowTray;
            showWidgetsCheckBox.IsChecked = activeSettings.ShowWidgets;
            showClockCheckBox.IsChecked = activeSettings.ShowSecondaryClock;
            fillMaximisedCheckBox.IsChecked = activeSettings.FillOnMaximise;
            fillAltTabCheckBox.IsChecked = activeSettings.FillOnTaskSwitch;
            showSegmentsOnHoverCheckBox.IsChecked = activeSettings.ShowSegmentsOnHover;
            compositionFixCheckBox.IsChecked = activeSettings.CompositionCompat;
            autoHideComboBox.SelectedIndex = GetSelectedSegmentLayout()?.AutoHide ?? 0;
            hoverDelaySlider.Value = activeSettings.HoverRevealDelayMs;
            hoverDelayInput.Text = activeSettings.HoverRevealDelayMs.ToString();
            hideDelaySlider.Value = activeSettings.HoverHideDelayMs;
            hideDelayInput.Text = activeSettings.HoverHideDelayMs.ToString();
            widgetWidthInput.Text = activeSettings.WidgetsWidth.ToString();
            clockWidthInput.Text = activeSettings.ClockWidth.ToString();
            taskbarDetails = Taskbar.GenerateTaskbarInfo(isWindows11);

            // Seed TTB detection before the first ApplyButton_Click so the initial
            // region updates already gate UpdateTranslucentTB correctly. The background
            // worker refreshes this every ~1s once it's running, but the first apply
            // happens here in the constructor, before the worker has had a chance.
            activeSettings.TtbRunning = Interaction.IsTranslucentTBRunning();

            ApplyButton_Click(null, null);


            if (!activeSettings.FillOnMaximise)
            {
                activeSettings.FillOnTaskSwitch = false;
                fillAltTabCheckBox.IsEnabled = false;
            }

            //Showhide the split mode help button
            if (!isWindows11 && activeSettings.IsDynamic)
            {
                splitHelpButton.Visibility = Visibility.Visible;
            }
            else
            {
                splitHelpButton.Visibility = Visibility.Hidden;
            }

            if (activeSettings.IsNotFirstLaunch != true)
            {
                activeSettings.IsNotFirstLaunch = true;
                AboutWindow aw = new AboutWindow();
                aw.expander0.IsExpanded = true;
                aw.ShowDialog();
                try
                {
                    Visibility = Visibility.Visible;
                }
                catch (InvalidOperationException)
                {

                }
                ShowMenuItem.Header = "Hide RoundedTB";
            }

            UpdateUi();
            RefreshTtbStatusUi();

            // Mirror the background worker's ~1s detection refresh into the
            // settings UI so the "TranslucentTB: detected" indicator follows
            // reality without the user having to close+reopen the window.
            // 1500 ms gives the worker (which polls every ~1000 ms) at least
            // one tick of headroom before we sample its result.
            DispatcherTimer ttbStatusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500),
            };
            ttbStatusTimer.Tick += (s, e) => RefreshTtbStatusUi();
            ttbStatusTimer.Start();
        }

        public void UpdateUi()
        {
            if (!activeSettings.ShowTray || activeSettings.ShowSegmentsOnHover)
            {
                trayRectStandIn.Opacity = 0.5;
            }
            else
            {
                trayRectStandIn.Opacity = 1;
            }

            if (!activeSettings.ShowWidgets || activeSettings.ShowSegmentsOnHover)
            {
                widgetsRectStandIn.Opacity = 0.5;
            }
            else
            {
                widgetsRectStandIn.Opacity = 1;
            }

            rectStands.AllWithoutCenterBar.ForEach(x => x.KeyControl.Visibility = Visibility.Hidden);
            if (activeSettings.IsCentred && activeSettings.IsWindows11 && activeSettings.IsDynamic)
            {
                taskbarRectStandIn.Margin = new Thickness(126, 0, 126, 5);
                rectStands.AllWithoutCenterBar.ForEach(x => x.KeyControl.Visibility = Visibility.Visible);
            }
            else if (activeSettings.IsDynamic)
            {
                taskbarRectStandIn.Margin = new Thickness(5, 0, 247, 5);
                trayRectStandIn.Visibility = Visibility.Visible;
            }
            else
            {
                taskbarRectStandIn.Margin = new Thickness(5, 210, 5, 5);
            }
        }

        public void AutoHide(bool enabled, List<Types.Taskbar> taskbarDetails)
        {
            // Note: we no longer call SetWorkspace here. The taskbar retains its
            // AppBar state of AlwaysOnTop (not ABS_AUTOHIDE), so Windows keeps
            // claiming its reserved height in the work area. Any SetWorkspace we
            // issue is reverted by the AppBar system within a second, and the
            // revert broadcasts a second WM_SETTINGCHANGE that snaps any window
            // extending past the taskbar upward - which the user sees as "windows
            // get pushed up every time RoundedTB launches".
            //
            // Show-on-desktop / Always-hide still work for windowed apps because
            // Windows only enforces the work area on maximize, not on free-floating
            // windows. Users who position apps over the taskbar area continue to
            // see them render there (with the taskbar fading to opacity=0 via the
            // background worker), and the push-up symptom is gone.
            Log.Information("AutoHide(enabled={Enabled}) called.", enabled);

            if (enabled)
            {
                foreach (Types.Taskbar taskbar in taskbarDetails)
                {
                    LocalPInvoke.SetWindowPos(taskbar.TaskbarHwnd, new IntPtr(-1), 0, 0, 0, 0, LocalPInvoke.SetWindowPosFlags.IgnoreMove | LocalPInvoke.SetWindowPosFlags.IgnoreResize);
                    Taskbar.SetTaskbarState(LocalPInvoke.AppBarStates.AlwaysOnTop, taskbar.TaskbarHwnd);
                }
            }
            else if (!enabled)
            {
                foreach (Types.Taskbar taskbar in taskbarDetails)
                {
                    LocalPInvoke.SetWindowPos(taskbar.TaskbarHwnd, new IntPtr(-1), 0, 0, 0, 0, LocalPInvoke.SetWindowPosFlags.IgnoreMove | LocalPInvoke.SetWindowPosFlags.IgnoreResize);
                }
            }
        }

        public void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            int mt = 0;
            int ml = 0;
            int mb = 0;
            int mr = 0;

            int widgetWidth = 0;
            int clockWidth = 0;

            {
                if ((!int.TryParse(mTopInput.Text, out mt) && mTopInput.Text != string.Empty)
                || (!int.TryParse(mLeftInput.Text, out ml) && mLeftInput.Text != string.Empty)
                || (!int.TryParse(mBottomInput.Text, out mb) && mBottomInput.Text != string.Empty)
                || (!int.TryParse(mRightInput.Text, out mr) && mRightInput.Text != string.Empty))
                {
                    return;
                }

                if ((!int.TryParse(widgetWidthInput.Text, out widgetWidth) && widgetWidthInput.Text != string.Empty)
                || (!int.TryParse(clockWidthInput.Text, out clockWidth) && clockWidthInput.Text != string.Empty))
                {
                    return;
                }
            }

            activeSettings.WidgetsWidth = widgetWidth;
            activeSettings.ClockWidth = clockWidth;

            // The combo box now mirrors the currently-selected segment's AutoHide via
            // autoHideComboBox_SelectionChanged. Each segment's value is written there,
            // so we no longer fold a single value into a global Settings.AutoHide.
            activeSettings.HoverRevealDelayMs = Convert.ToInt32(Math.Round(hoverDelaySlider.Value));
            activeSettings.HoverHideDelayMs = Convert.ToInt32(Math.Round(hideDelaySlider.Value));
            activeSettings.IsDynamic = (bool)dynamicCheckBox.IsChecked;
            activeSettings.IsCentred = Taskbar.CheckIfCentred();
            activeSettings.ShowTray = (bool)showTrayCheckBox.IsChecked;
            activeSettings.ShowWidgets = (bool)showWidgetsCheckBox.IsChecked;
            activeSettings.ShowSecondaryClock = (bool)showClockCheckBox.IsChecked;
            activeSettings.CompositionCompat = (bool)compositionFixCheckBox.IsChecked;
            activeSettings.FillOnMaximise = (bool)fillMaximisedCheckBox.IsChecked;
            activeSettings.FillOnTaskSwitch = (bool)fillAltTabCheckBox.IsChecked;
            activeSettings.ShowSegmentsOnHover = (bool)showSegmentsOnHoverCheckBox.IsChecked;

            try
            {
                foreach (Types.Taskbar taskbar in taskbarDetails)
                {
                    // Mirror the poll loop's fill guard. With FillOnMaximise enabled and a
                    // maximised window on this taskbar's monitor, the poll has already called
                    // ResetTaskbar to strip the region — its rect cache was frozen at the
                    // pre-fill values. Running UpdateSimple/Dynamic here would redraw a region
                    // sized to those stale bounds, visibly shrinking the taskbar. Keep it
                    // reset and mark Ignored so the poll recomputes cleanly when the window
                    // unmaximises.
                    if (Taskbar.TaskbarShouldBeFilled(taskbar.TaskbarHwnd, activeSettings))
                    {
                        Taskbar.ResetTaskbar(taskbar, activeSettings);
                        taskbar.Ignored = true;
                        continue;
                    }

                    int isFullTest = taskbar.TrayRect.Left - taskbar.AppListRect.Right;
                    if (!activeSettings.IsDynamic || (isFullTest <= taskbar.ScaleFactor * 25 && isFullTest > 0 && taskbar.TrayRect.Left != 0))
                    {
                        Taskbar.UpdateSimpleTaskbar(taskbar, activeSettings);
                    }
                    else
                    {
                        Taskbar.UpdateDynamicTaskbar(taskbar, activeSettings);
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                Log.Warning(ex, "Taskbar update failed; likely a transient collection-modified race");
            }


            if (taskbarThread.IsBusy == false)
            {
                taskbarThread.RunWorkerAsync((mt, ml, mb, mr, 0));
            }
            else
            {
                taskbarThread.CancelAsync();
                while (taskbarThread.IsBusy == true)
                {
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(100);
                }
                taskbarThread.RunWorkerAsync((mt, ml, mb, mr, 0));
            }

            // Drive the per-window AlwaysOnTop / work-area flip off "any segment hides"
            // rather than the legacy single AutoHide. The actual per-segment show/hide
            // happens in the background worker via region clipping; this block only
            // covers the once-per-transition side effects on the taskbar window itself.
            bool nowAnyAutoHide = AnyAutoHideEnabled();
            if (lastAppliedAnyAutoHide != nowAnyAutoHide)
            {
                if (nowAnyAutoHide)
                {
                    AutoHide(true, taskbarDetails);
                }
                else if (lastAppliedAnyAutoHide == true)
                {
                    // Only restore when we previously enabled it. Skipping the null→false
                    // path matters: at startup with no autohide configured, the OS work
                    // area is already correct and calling SetWorkspace would broadcast
                    // WM_SETTINGCHANGE, re-flowing foreground windows upward.
                    AutoHide(false, taskbarDetails);
                }
                lastAppliedAnyAutoHide = nowAnyAutoHide;
            }
            interaction.WriteJSON();
            UpdateUi();

        }

        protected override void OnClosing(CancelEventArgs e)
        {
            Log.Information($"MainWindow.OnClosing called. shouldReallyDieNoReally: {shouldReallyDieNoReally}");

            if (shouldReallyDieNoReally == false)
            {
                Log.Information("Cancelling window close, hiding window instead");
                e.Cancel = true;
                Visibility = Visibility.Hidden;
                ShowMenuItem.Header = "Show RoundedTB";
                return;
            }

            Log.Information("Allowing window close - application will exit");

            try
            {
                taskbarThread.CancelAsync();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to cancel background worker on close");
            }
            while (taskbarThread.IsBusy == true)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(100);
            }

            try
            {
                foreach (var tbDeets in taskbarDetails)
                {
                    Taskbar.ResetTaskbar(tbDeets, activeSettings);
                }
                if (lastAppliedAnyAutoHide == true)
                {
                    AutoHide(false, taskbarDetails);
                }
            }
            catch (InvalidOperationException ex)
            {
                Log.Warning(ex, "Taskbar structure changed during exit cleanup");
            }

            if (!isAlreadyRunning)
            {
                interaction.WriteJSON();
            }

            DisposeTrayIcon();
            base.OnClosing(e);
        }

        private void CloseMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Log.Information("CloseMenuItem clicked - exiting RoundedTB");
            shouldReallyDieNoReally = true;

            // Do the taskbar cleanup ourselves (same as MainWindow.OnClosing's
            // shouldReallyDieNoReally=true path) in case the WPF shutdown chain
            // doesn't reach OnClosing - e.g. when the MainWindow is already in
            // a zombie state from an earlier X press.
            try { taskbarThread.CancelAsync(); } catch { }

            try
            {
                if (taskbarDetails != null)
                {
                    foreach (var tbDeets in taskbarDetails)
                    {
                        try { Taskbar.ResetTaskbar(tbDeets, activeSettings); } catch { }
                    }
                }
            }
            catch { }

            try { if (!isAlreadyRunning) interaction.WriteJSON(); } catch { }
            try { DisposeTrayIcon(); } catch { }

            try { (System.Windows.Application.Current as App)?.Shutdown(); } catch { }

            // Force terminate after a short grace period. WPF's shutdown can
            // hang when the dispatcher is in a weird state (the CE shutdown-
            // prevention trick can leave it that way after a prior X press).
            // Environment.Exit bypasses any remaining plumbing - we've already
            // done the visible cleanup (reset regions, saved JSON, dropped
            // tray icon).
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500);
                Environment.Exit(0);
            });
        }

        public void ShowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (IsVisible == false)
                {
                    Visibility = Visibility.Visible;
                    ShowMenuItem.Header = "Hide RoundedTB";
                }
                else
                {
                    var app = App.Current;
                    if (app != null && app.Windows != null)
                    {
                        for (int windowCount = app.Windows.Count - 1; windowCount >= 0; windowCount--)
                        {
                            var window = app.Windows[windowCount];
                            if (window != null && window != this)
                            {
                                try { window.Close(); } catch { }
                            }
                        }
                    }
                    Visibility = Visibility.Hidden;
                    ShowMenuItem.Header = "Show RoundedTB";
                }
            }
            catch (InvalidOperationException ex)
            {
                // Window may have been closed by the WPF shutdown plumbing despite
                // OnClosing's cancel (CE's shouldReallyDieNoReally logic is racy).
                // Setting Visibility on a closed window throws; just log and recover.
                Serilog.Log.Warning(ex, "ShowMenuItem_Click: window is closed, cannot toggle visibility");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "ShowMenuItem_Click unexpected error");
            }
        }

        private void SetupTrayIcon()
        {
            if (_trayIcon != null)
            {
                return;
            }

            _trayIcon = new TaskbarIcon
            {
                Icon = LoadTrayIcon(),
                ToolTipText = "RoundedTB",
                ContextMenu = TrayContextMenu,
                Visibility = Visibility.Visible
            };

            _trayIcon.TrayLeftMouseUp += TrayIcon_OnLeftClick;
        }

        private void TrayIcon_OnLeftClick(object sender, RoutedEventArgs e)
        {
            if (IsVisible)
            {
                Visibility = Visibility.Hidden;
                ShowMenuItem.Header = "Show RoundedTB";
            }
            else
            {
                Visibility = Visibility.Visible;
                ShowMenuItem.Header = "Hide RoundedTB";
                Activate();
            }
        }

        private DrawingIcon LoadTrayIcon()
        {
            try
            {
                var streamInfo = System.Windows.Application.GetResourceStream(new Uri(Branding.IconResourcePath));
                if (streamInfo != null)
                {
                    return new DrawingIcon(streamInfo.Stream);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load tray icon from resources, falling back to executable icon");
            }

            try
            {
                // Prefer ProcessPath: Assembly.Location returns "" under single-file publish.
                string exePath = Environment.ProcessPath ?? System.Windows.Application.ResourceAssembly.Location;
                var exeIcon = DrawingIcon.ExtractAssociatedIcon(exePath);
                if (exeIcon != null)
                {
                    return exeIcon;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load tray icon from executable");
            }

            return System.Drawing.SystemIcons.Application;
        }

        private void DisposeTrayIcon()
        {
            if (_trayIcon != null)
            {
                _trayIcon.TrayLeftMouseUp -= TrayIcon_OnLeftClick;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }

        private async void Startup_Clicked(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Startup toggled");
            if (IsRunningAsUWP())
            {
                await StartupToggle();
                await StartupInit(false);
            }
            else
            {
                // When Click event fires, checkbox has already toggled to the new state.
                // Use the new state directly (don't invert it).
                var enableStartup = StartupCheckBox.IsChecked == true;
                SetStartupEnabled(enableStartup);
            }
        }

        async Task StartupToggle()
        {
            StartupTask startupTask = await StartupTask.GetAsync("RTB"); // Pass the task ID you specified in the appxmanifest file
            switch (startupTask.State)
            {
                case StartupTaskState.Disabled:
                    StartupTaskState newState = await startupTask.RequestEnableAsync();
                    StartupCheckBox.IsEnabled = true;
                    break;

                case StartupTaskState.DisabledByUser:
                    StartupCheckBox.IsEnabled = false;
                    break;

                case StartupTaskState.EnabledByPolicy:
                    StartupCheckBox.IsEnabled = false;
                    break;

                case StartupTaskState.DisabledByPolicy:
                    StartupCheckBox.IsEnabled = false;
                    break;

                case StartupTaskState.Enabled:
                    startupTask.Disable();
                    StartupCheckBox.IsEnabled = true;
                    break;
            }
        }

        private void SetStartupEnabled(bool enable)
        {
            try
            {
                if (enable)
                {
                    CreateStartupShortcut();
                    SetRunRegistryValue();
                }
                else
                {
                    RemoveStartupShortcut();
                    RemoveRunRegistryValue();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to update startup registration");
            }
            finally
            {
                RefreshStartupCheckboxFromSystem();
            }
        }

        private void RefreshStartupCheckboxFromSystem()
        {
            try
            {
                bool shortcutExists = File.Exists(GetStartupShortcutPath());
                bool registryEnabled = false;

                using (var key = Registry.CurrentUser.OpenSubKey(StartupRunRegistryPath, false))
                {
                    var value = key?.GetValue(StartupRunValueName) as string;
                    registryEnabled = !string.IsNullOrWhiteSpace(value);
                }

                StartupCheckBox.IsChecked = shortcutExists || registryEnabled;
                StartupCheckBox.Content = "Run at startup";
                StartupCheckBox.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to read startup registration");
            }
        }

        private void CreateStartupShortcut()
        {
            string shortcutPath = GetStartupShortcutPath();
            string shortcutFolder = Path.GetDirectoryName(shortcutPath)!;
            if (!Directory.Exists(shortcutFolder))
            {
                Directory.CreateDirectory(shortcutFolder);
            }

            // Use dynamic to avoid compile-time COM dependency
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shellClass = Activator.CreateInstance(shellType);

            dynamic shortcut = shellClass.CreateShortcut(shortcutPath);
            string exePath = Environment.ProcessPath ?? System.Windows.Application.ResourceAssembly.Location;

            shortcut.TargetPath = exePath;
            shortcut.IconLocation = exePath;
            shortcut.Arguments = "";
            shortcut.Description = "Start RoundedTB";
            shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
            shortcut.Save();
        }

        private void RemoveStartupShortcut()
        {
            string shortcutPath = GetStartupShortcutPath();
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }
        }

        private string GetStartupShortcutPath()
        {
            string shortcutFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return Path.Combine(shortcutFolder, StartupShortcutName);
        }

        private void SetRunRegistryValue()
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRunRegistryPath, writable: true) ??
                            Registry.CurrentUser.CreateSubKey(StartupRunRegistryPath);
            string exePath = Environment.ProcessPath ?? System.Windows.Application.ResourceAssembly.Location;
            key.SetValue(StartupRunValueName, $"\"{exePath}\"");
        }

        private void RemoveRunRegistryValue()
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRunRegistryPath, writable: true);
            key?.DeleteValue(StartupRunValueName, false);
        }

        async Task StartupInit(bool clean)
        {
            StartupTask startupTask = await StartupTask.GetAsync("RTB");
            switch (startupTask.State)
            {
                case StartupTaskState.Disabled:
                    StartupCheckBox.IsChecked = false;
                    StartupCheckBox.IsEnabled = true;
                    if (clean)
                    {
                        Visibility = Visibility.Visible;
                        ShowMenuItem.Header = "Hide RoundedTB";
                    }
                    StartupCheckBox.Content = "Run at startup";
                    break;

                case StartupTaskState.DisabledByUser:
                    StartupCheckBox.IsChecked = false;
                    StartupCheckBox.IsEnabled = false;
                    if (clean)
                    {
                        Visibility = Visibility.Visible;
                        ShowMenuItem.Header = "Hide RoundedTB";
                    }
                    StartupCheckBox.Content = "Startup unavailable";
                    break;

                case StartupTaskState.EnabledByPolicy:
                    StartupCheckBox.IsChecked = true;
                    StartupCheckBox.IsEnabled = false;
                    if (clean)
                    {
                        Visibility = Visibility.Hidden;
                        ShowMenuItem.Header = "Show RoundedTB";
                    }
                    StartupCheckBox.Content = "Startup mandatory";
                    break;

                case StartupTaskState.DisabledByPolicy:
                    StartupCheckBox.IsChecked = false;
                    StartupCheckBox.IsEnabled = false;
                    if (clean)
                    {
                        Visibility = Visibility.Visible;
                        ShowMenuItem.Header = "Hide RoundedTB";
                    }
                    StartupCheckBox.Content = "Startup unavailable";
                    break;

                case StartupTaskState.Enabled:
                    StartupCheckBox.IsChecked = true;
                    StartupCheckBox.IsEnabled = true;
                    if (clean)
                    {
                        Visibility = Visibility.Hidden;
                        ShowMenuItem.Header = "Show RoundedTB";
                    }
                    StartupCheckBox.Content = "Run at startup";
                    break;
            }
        }

        // Checks if running as a UWP app
        public bool IsRunningAsUWP()
        {
            try
            {
                Helpers helpers = new Helpers();
                return helpers.IsRunningAsUwp();
            }
            catch (Exception)
            {
                return false;
            }

        }

        private void DebugMenuItem_Click(object sender, RoutedEventArgs e)
        {
            IntPtr hwndNext = LocalPInvoke.FindWindowExA(taskbarDetails[0].TaskbarHwnd, IntPtr.Zero, "Start", null);
            List<IntPtr> floatingMilkshakesBitsOfTaskbar = new List<IntPtr>();
            floatingMilkshakesBitsOfTaskbar.Add(hwndNext);
            while (true)
            {
                hwndNext = LocalPInvoke.FindWindowExA(taskbarDetails[0].TaskbarHwnd, hwndNext, null, null);
                if (floatingMilkshakesBitsOfTaskbar.Contains(hwndNext))
                {
                    break;
                }
                floatingMilkshakesBitsOfTaskbar.Add(hwndNext);

            }
            foreach (IntPtr hwnd in floatingMilkshakesBitsOfTaskbar)
            {
                LocalPInvoke.GetWindowRect(hwnd, out LocalPInvoke.RECT rect);
                LocalPInvoke.MoveWindow(hwnd, rect.Left + 50, rect.Top, (rect.Right + 50) - (rect.Left + 50), rect.Bottom - rect.Top, true);
            }
        }

        private async void ContextMenu_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (IsRunningAsUWP())
            {
                await StartupInit(false);
            }
        }

        private void dynamicCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            centredCheckBox.IsEnabled = true;
            showSegmentsOnHoverCheckBox.IsEnabled = true;
            showSegmentsOnHoverCheckBox.IsChecked = false;
            showTrayCheckBox.IsEnabled = true;
            showTrayCheckBox.IsChecked = true;

            if (!isWindows11)
            {
                splitHelpButton.Visibility = Visibility.Visible;
                if (Opacity > 0.5)
                {
                    splitHelpButton_Click(null, null);
                }
            }

        }

        private void dynamicCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {

            centredCheckBox.IsEnabled = false;
            centredCheckBox.IsChecked = false;
            showSegmentsOnHoverCheckBox.IsEnabled = false;
            showSegmentsOnHoverCheckBox.IsChecked = false;
            showTrayCheckBox.IsEnabled = false;
            showTrayCheckBox.IsChecked = false;

            if (!isWindows11)
            {
                splitHelpButton.Visibility = Visibility.Hidden;
            }
        }

        private void cornerRadiusSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            int check = Convert.ToInt32(Math.Round(cornerRadiusSlider.Value));
            cornerRadiusInput.Text = check.ToString();

            switch (selectedSegment)
            {
                default:
                    break;

                case 0:
                    activeSettings.SimpleTaskbarLayout.CornerRadius = check;
                    break;

                case 1:
                    activeSettings.DynamicAppListLayout.CornerRadius = check;
                    break;

                case 2:
                    activeSettings.DynamicTrayLayout.CornerRadius = check;
                    break;

                case 3:
                    activeSettings.DynamicWidgetsLayout.CornerRadius = check;
                    break;

                case 4:
                    activeSettings.DynamicSecondaryClockLayout.CornerRadius = check;
                    break;
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            Debug.WriteLine("AAAAA");
            base.OnSourceInitialized(e);


            IntPtr handle = new WindowInteropHelper(this).Handle;
            source = HwndSource.FromHwnd(handle);
            source.AddHook(interaction.HwndHook);
            bool wtf = LocalPInvoke.RegisterHotKey(handle, 9000, 0x8, 0x71);
            Debug.WriteLine("KEY: " + wtf);
            Debug.WriteLine(handle);
            Debug.WriteLine((int)Types.KeyModifier.WinKey);
            Debug.WriteLine(System.Windows.Forms.Keys.J.GetHashCode());
            Visibility = Visibility.Hidden;
            Opacity = 1;
        }

        private void splitHelpButton_Click(object sender, RoutedEventArgs e)
        {
            Infobox ib = new Infobox();
            ib.Title = "RoundedTB - Split mode configuration";
            ib.titleBlock.Text = "How to use Split Mode";
            ib.bodyBlock.Text = "Split mode has a couple of limitations and requires a small amount of setup to get working properly.\n\nLimitations:\n1) Split mode doesn't resize itself automatically. This feature will be coming to RoundedTB for Windows 10 in the future.\n2) Toolbars are not compatible with split mode currently, and will need to be disabled apart from one (more on that in a moment).\n3) Split mode only works when the taskbar is horizontal at the top or bottom of the screen.\n\nSetup:\n1) Right-click the taskbar and disable \"Lock the taskbar\".\n2) Right-click it again and turn off any existing toolbars.\n3) Right-click a third time, select Toolbars > Desktop.\n4) Use the small || handle to resize the taskbar as you please.";
            ib.ShowDialog();
        }

        private void compositionFixCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (Opacity > 0.01)
            {
                Infobox ib = new Infobox();
                ib.Height = 450;
                ib.Title = "RoundedTB - TranslucentTB integration";
                ib.titleBlock.Text = "Working with TranslucentTB";
                ib.bodyBlock.Text = "\nTranslucentTB customises the opacity, blur and colour of the taskbar with finer control than RoundedTB itself. Leaving this option enabled lets the two work together: RoundedTB tells TranslucentTB to redraw whenever it reshapes the taskbar.\n\nThe integration only fires when TranslucentTB is actually running — RoundedTB now auto-detects it, so installing or quitting TranslucentTB while RoundedTB is up just works.\n\nThe handoff is necessary because of how Windows composites the taskbar; you might see minor flicker when the taskbar reshapes. If it bothers you, uncheck this box to disable the integration.\n\nGet TranslucentTB from the Microsoft Store: https://apps.microsoft.com/detail/9pf4kz2vn4w9";
                ib.ShowDialog();
            }
        }

        // Updates the inline TranslucentTB status indicator below the integration
        // checkbox. Called from a DispatcherTimer so the text follows TtbRunning
        // (refreshed by the background worker every ~1s) without bouncing through
        // INotifyPropertyChanged plumbing for one read-only label.
        private void RefreshTtbStatusUi()
        {
            if (ttbStatusText == null || ttbStatusLink == null)
            {
                return;
            }
            bool running = activeSettings?.TtbRunning ?? false;
            if (running)
            {
                ttbStatusText.Text = "TranslucentTB: detected ✓";
                ttbStatusText.Visibility = Visibility.Visible;
                ttbStatusLink.Visibility = Visibility.Collapsed;
            }
            else
            {
                ttbStatusText.Visibility = Visibility.Collapsed;
                ttbStatusLink.Visibility = Visibility.Visible;
            }
        }

        private void TtbStatusHyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.ToString(),
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to open TranslucentTB hyperlink");
            }
            e.Handled = true;
        }

        private void aboutButton_Click(object sender, RoutedEventArgs e)
        {
            AboutWindow aw = new AboutWindow();
            aw.ShowDialog();
        }

        private void fillMaximisedCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (isWindows11)
            {
                fillAltTabCheckBox.IsEnabled = true;
            }
        }

        private void fillMaximisedCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            fillAltTabCheckBox.IsEnabled = false;
            fillAltTabCheckBox.IsChecked = false;

        }

        private void showSegmentsOnHoverCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            showTrayCheckBox.IsEnabled = false;
            showTrayCheckBox.IsChecked = false;

            showWidgetsCheckBox.IsEnabled = false;
            showWidgetsCheckBox.IsChecked = false;
        }

        private void showSegmentsOnHoverCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            showTrayCheckBox.IsEnabled = true;
            showTrayCheckBox.IsChecked = true;

            showWidgetsCheckBox.IsEnabled = true;
            showWidgetsCheckBox.IsChecked = true;
        }

        private void SetLayoutInput(Types.SegmentSettings layout)
        {
            cornerRadiusInput.Text = layout.CornerRadius.ToString();
            cornerRadiusSlider.Value = layout.CornerRadius;
            mTopInput.Text = layout.MarginTop.ToString();
            mLeftInput.Text = layout.MarginLeft.ToString();
            mBottomInput.Text = layout.MarginBottom.ToString();
            mRightInput.Text = layout.MarginRight.ToString();
            // Mirror the corner-radius / margin pattern: when the user clicks a different
            // segment standin, the autohide combo follows along to that segment's value.
            // The SelectionChanged handler then writes back to the same segment, which is
            // a no-op (we just set it from the same source), so this doesn't cause loops.
            autoHideComboBox.SelectedIndex = layout.AutoHide;
        }

        // Maps the current selectedSegment to the matching SegmentSettings instance so
        // the combo-box and other shared inputs can read/write the right field without
        // duplicating the switch in every handler.
        private Types.SegmentSettings GetSelectedSegmentLayout()
        {
            return selectedSegment switch
            {
                0 => activeSettings.SimpleTaskbarLayout,
                1 => activeSettings.DynamicAppListLayout,
                2 => activeSettings.DynamicTrayLayout,
                3 => activeSettings.DynamicWidgetsLayout,
                4 => activeSettings.DynamicSecondaryClockLayout,
                _ => null,
            };
        }

        // True if any segment that's in play for the current mode has autohide enabled.
        // Used to drive the once-per-transition AlwaysOnTop / work-area side effects;
        // the actual show/hide of individual segments lives in the background worker.
        private bool AnyAutoHideEnabled()
        {
            if (activeSettings.IsDynamic)
            {
                return (activeSettings.DynamicAppListLayout?.AutoHide ?? 0) > 0
                    || (activeSettings.DynamicTrayLayout?.AutoHide ?? 0) > 0
                    || (activeSettings.DynamicWidgetsLayout?.AutoHide ?? 0) > 0
                    || (activeSettings.DynamicSecondaryClockLayout?.AutoHide ?? 0) > 0;
            }
            return (activeSettings.SimpleTaskbarLayout?.AutoHide ?? 0) > 0;
        }

        private void autoHideComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = autoHideComboBox.SelectedIndex;
            if (idx < 0)
            {
                return;
            }
            Types.SegmentSettings layout = GetSelectedSegmentLayout();
            if (layout != null)
            {
                layout.AutoHide = idx;
            }
        }

        private void taskbarRectStandIn_Click(object sender, RoutedEventArgs e)
        {
            rectStands.Focus(rectStands.CenterBar);

            if (activeSettings.IsDynamic)
            {
                selectedSegment = 1;
                SetLayoutInput(activeSettings.DynamicAppListLayout);
            }
            else
            {
                selectedSegment = 0;
                SetLayoutInput(activeSettings.SimpleTaskbarLayout);
            }
        }

        private void trayRectStandIn_Click(object sender, RoutedEventArgs e)
        {
            rectStands.Focus(rectStands.TaskTray);

            selectedSegment = 2;
            SetLayoutInput(activeSettings.DynamicTrayLayout);
        }

        private void widgetsRectStandIn_Click(object sender, RoutedEventArgs e)
        {
            rectStands.Focus(rectStands.Widgets);

            selectedSegment = 3;
            SetLayoutInput(activeSettings.DynamicWidgetsLayout);
        }

        private void clockRectStandIn_Click(object sender, RoutedEventArgs e)
        {
            rectStands.Focus(rectStands.Clock);

            selectedSegment = 4;
            SetLayoutInput(activeSettings.DynamicSecondaryClockLayout);
        }

        private void mTopInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(mTopInput.Text, out int check) && mTopInput.Text != string.Empty)
            {
                switch (selectedSegment)
                {
                    default:
                        break;

                    case 0:
                        activeSettings.SimpleTaskbarLayout.MarginTop = check;
                        break;

                    case 1:
                        activeSettings.DynamicAppListLayout.MarginTop = check;
                        break;

                    case 2:
                        activeSettings.DynamicTrayLayout.MarginTop = check;
                        break;

                    case 3:
                        activeSettings.DynamicWidgetsLayout.MarginTop = check;
                        break;

                    case 4:
                        activeSettings.DynamicSecondaryClockLayout.MarginTop = check;
                        break;
                }
            }
        }

        private void mBottomInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(mBottomInput.Text, out int check) && mBottomInput.Text != string.Empty)
            {
                switch (selectedSegment)
                {
                    default:
                        break;

                    case 0:
                        activeSettings.SimpleTaskbarLayout.MarginBottom = check;
                        break;

                    case 1:
                        activeSettings.DynamicAppListLayout.MarginBottom = check;
                        break;

                    case 2:
                        activeSettings.DynamicTrayLayout.MarginBottom = check;
                        break;

                    case 3:
                        activeSettings.DynamicWidgetsLayout.MarginBottom = check;
                        break;

                    case 4:
                        activeSettings.DynamicSecondaryClockLayout.MarginBottom = check;
                        break;
                }
            }
        }

        private void mLeftInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(mLeftInput.Text, out int check) && mLeftInput.Text != string.Empty)
            {
                switch (selectedSegment)
                {
                    default:
                        break;

                    case 0:
                        activeSettings.SimpleTaskbarLayout.MarginLeft = check;
                        break;

                    case 1:
                        activeSettings.DynamicAppListLayout.MarginLeft = check;
                        break;

                    case 2:
                        activeSettings.DynamicTrayLayout.MarginLeft = check;
                        break;

                    case 3:
                        activeSettings.DynamicWidgetsLayout.MarginLeft = check;
                        break;

                    case 4:
                        activeSettings.DynamicSecondaryClockLayout.MarginLeft = check;
                        break;
                }
            }
        }

        private void mRightInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(mRightInput.Text, out int check) && mRightInput.Text != string.Empty)
            {
                switch (selectedSegment)
                {
                    default:
                        break;

                    case 0:
                        activeSettings.SimpleTaskbarLayout.MarginRight = check;
                        break;

                    case 1:
                        activeSettings.DynamicAppListLayout.MarginRight = check;
                        break;

                    case 2:
                        activeSettings.DynamicTrayLayout.MarginRight = check;
                        break;

                    case 3:
                        activeSettings.DynamicWidgetsLayout.MarginRight = check;
                        break;

                    case 4:
                        activeSettings.DynamicSecondaryClockLayout.MarginRight = check;
                        break;
                }
            }
        }

        private void cornerRadiusInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(cornerRadiusInput.Text, out int check) && cornerRadiusInput.Text != string.Empty)
            {
                switch (selectedSegment)
                {
                    default:
                        break;

                    case 0:
                        activeSettings.SimpleTaskbarLayout.CornerRadius = check;
                        break;

                    case 1:
                        activeSettings.DynamicAppListLayout.CornerRadius = check;
                        break;

                    case 2:
                        activeSettings.DynamicTrayLayout.CornerRadius = check;
                        break;

                    case 3:
                        activeSettings.DynamicWidgetsLayout.CornerRadius = check;
                        break;

                    case 4:
                        activeSettings.DynamicSecondaryClockLayout.CornerRadius = check;
                        break;
                }

                cornerRadiusSlider.Value = check;
            }
        }

        // Shared by every slider/input mirror pair below. WPF dispatches UI
        // events on a single thread so one guard is enough to break the
        // TextChanged -> ValueChanged -> TextChanged loop; each direction
        // flips the flag around the assignment that would otherwise re-enter.
        private bool _syncingSliderInput = false;

        private void SyncTextToSlider(TextBox input, Slider slider)
        {
            if (_syncingSliderInput) return;
            if (int.TryParse(input.Text, out int v)
                && v >= slider.Minimum && v <= slider.Maximum)
            {
                _syncingSliderInput = true;
                try { slider.Value = v; }
                finally { _syncingSliderInput = false; }
            }
        }

        private void SyncSliderToText(Slider slider, TextBox input)
        {
            if (_syncingSliderInput || input == null) return;
            _syncingSliderInput = true;
            try { input.Text = Math.Round(slider.Value).ToString(); }
            finally { _syncingSliderInput = false; }
        }

        private void cornerRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => SyncSliderToText(cornerRadiusSlider, cornerRadiusInput);

        private void cornerRadiusInput_TextChanged(object sender, TextChangedEventArgs e)
            => SyncTextToSlider(cornerRadiusInput, cornerRadiusSlider);

        private void hoverDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => SyncSliderToText(hoverDelaySlider, hoverDelayInput);

        private void hoverDelayInput_TextChanged(object sender, TextChangedEventArgs e)
            => SyncTextToSlider(hoverDelayInput, hoverDelaySlider);

        private void hoverDelayInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(hoverDelayInput.Text, out int ms))
            {
                if (ms < hoverDelaySlider.Minimum) ms = (int)hoverDelaySlider.Minimum;
                if (ms > hoverDelaySlider.Maximum) ms = (int)hoverDelaySlider.Maximum;
                hoverDelayInput.Text = ms.ToString();
                hoverDelaySlider.Value = ms;
            }
            else
            {
                hoverDelayInput.Text = Math.Round(hoverDelaySlider.Value).ToString();
            }
        }

        private void hideDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
            => SyncSliderToText(hideDelaySlider, hideDelayInput);

        private void hideDelayInput_TextChanged(object sender, TextChangedEventArgs e)
            => SyncTextToSlider(hideDelayInput, hideDelaySlider);

        private void hideDelayInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(hideDelayInput.Text, out int ms))
            {
                if (ms < hideDelaySlider.Minimum) ms = (int)hideDelaySlider.Minimum;
                if (ms > hideDelaySlider.Maximum) ms = (int)hideDelaySlider.Maximum;
                hideDelayInput.Text = ms.ToString();
                hideDelaySlider.Value = ms;
            }
            else
            {
                hideDelayInput.Text = Math.Round(hideDelaySlider.Value).ToString();
            }
        }
    }
}
