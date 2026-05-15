using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace RoundedTB
{
    public class Background
    {
        private struct ReloadChecker
        {
            public bool IsReload { get; set; }
        }

        // Just have a reference point for the Dispatcher
        private MainWindow _mw;
        public MainWindow mw
        {
            get
            {
                if (_mw == null)
                {
                    // Try to get the MainWindow when first accessed
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow != null)
                    {
                        _mw = mainWindow;
                    }
                    else
                    {
                        // If MainWindow is null, search for the MainWindow among all windows
                        foreach (Window window in Application.Current.Windows)
                        {
                            if (window is MainWindow mainWnd)
                            {
                                _mw = mainWnd;
                                break;
                            }
                        }
                    }
                }
                return _mw;
            }
        }

        bool redrawOverride = false;
        int infrequentCount = 0;

#if DEBUG
        // Diagnostic state for autohide. Tracked per-taskbar by index so we
        // only log on transitions (not every 100ms tick). Debug-only so
        // Release/Canary builds don't spend cycles or disk on chatter.
        private readonly Dictionary<int, byte> _lastLoggedOpacity = new();
        private readonly Dictionary<int, bool> _lastLoggedLayeredMissing = new();
        private readonly Dictionary<int, bool> _lastLoggedGLWAFailed = new();
        private bool _loggedAutohideEntered = false;
#endif

        public Background()
        {
            // Reference lookup is now deferred until mw property is accessed
        }

        public void SetMainWindow(MainWindow mainWindow)
        {
            _mw = mainWindow;
        }


        // Computes the screen-space rectangle where the given dynamic-mode segment WOULD
        // appear if it were visible. Used for cursor hover-testing — works whether or not
        // the segment is currently region-clipped, because cursor coords are absolute.
        // 'forHidden' replaces the body with a 2px reveal strip at the bottom of the
        // taskbar so the user can still trigger an unhide by sliding to the screen edge.
        private static LocalPInvoke.RECT GetSegmentScreenRect(Types.Taskbar tb, Types.Settings settings, int segment, bool forHidden)
        {
            LocalPInvoke.RECT rect = tb.TaskbarRect;
            int widgetsW = Convert.ToInt32(settings.WidgetsWidth * tb.ScaleFactor);
            int clockW = Convert.ToInt32(settings.ClockWidth * tb.ScaleFactor);

            switch (segment)
            {
                case 1: // AppList — middle of the taskbar, excluding ranges occupied by widgets/tray/clock so an "always show" tray doesn't trigger AppList's hover group.
                    int left = rect.Left;
                    int right = rect.Right;
                    if (settings.ShowWidgets)
                    {
                        left = rect.Left + widgetsW;
                    }
                    if (settings.ShowTray && tb.TrayHwnd != IntPtr.Zero && tb.TrayRect.Left > 0)
                    {
                        right = tb.TrayRect.Left;
                    }
                    if (settings.ShowSecondaryClock && tb.IsSecondary)
                    {
                        right = Math.Min(right, rect.Right - clockW);
                    }
                    rect = new LocalPInvoke.RECT { Left = left, Top = rect.Top, Right = right, Bottom = rect.Bottom };
                    break;
                case 2: // Tray — already screen coords on the Taskbar struct.
                    rect = tb.TrayRect;
                    break;
                case 3: // Widgets — leftmost WidgetsWidth pixels.
                    rect = new LocalPInvoke.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Left + widgetsW, Bottom = rect.Bottom };
                    break;
                case 4: // Secondary clock — rightmost ClockWidth pixels (only meaningful on secondary taskbars).
                    rect = new LocalPInvoke.RECT { Left = rect.Right - clockW, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
                    break;
            }

            if (forHidden && rect.Bottom > rect.Top)
            {
                // Same trick the legacy whole-window autohide used: a 2px strip flush with
                // the bottom of the screen so the user can sweep to the edge to reveal,
                // even when the segment is fully region-clipped.
                rect.Top = rect.Bottom - 2;
            }
            return rect;
        }

        // Tracks one autohide group's hover dwell. A group is "any of its members is hovered
        // right now"; when it's been continuously true for HoverRevealDelayMs the group
        // reveals, and when it's been continuously false for HoverHideDelayMs it hides.
        // The visible? state is sticky across ticks via the timer fields on Types.Taskbar.
        private static bool UpdateGroupReveal(Types.Taskbar tb, Types.Settings settings, bool anyHovered, ref DateTime? startedAt, ref DateTime? endedAt, bool currentlyRevealed)
        {
            double revealMs = settings.HoverRevealDelayMs;
            double hideMs = settings.HoverHideDelayMs;
            DateTime now = DateTime.UtcNow;

            if (anyHovered)
            {
                endedAt = null;
                if (currentlyRevealed)
                {
                    // Already revealed — stay revealed and reset the start timer for the
                    // next hidden→revealed transition.
                    startedAt = null;
                    return true;
                }
                if (revealMs <= 0)
                {
                    startedAt = null;
                    return true;
                }
                if (startedAt == null)
                {
                    startedAt = now;
                    return false;
                }
                if ((now - startedAt.Value).TotalMilliseconds >= revealMs)
                {
                    startedAt = null;
                    return true;
                }
                return false;
            }
            else
            {
                startedAt = null;
                if (!currentlyRevealed)
                {
                    endedAt = null;
                    return false;
                }
                if (hideMs <= 0)
                {
                    endedAt = null;
                    return false;
                }
                if (endedAt == null)
                {
                    endedAt = now;
                    return true;
                }
                if ((now - endedAt.Value).TotalMilliseconds >= hideMs)
                {
                    endedAt = null;
                    return false;
                }
                return true;
            }
        }

        private static bool IsForegroundDesktop()
        {
            IntPtr fg = LocalPInvoke.GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return false;
            }
            StringBuilder fgClass = new StringBuilder(64);
            LocalPInvoke.GetClassName(fg, fgClass, fgClass.Capacity);
            string c = fgClass.ToString();
            return c == "Progman" || c == "WorkerW";
        }

        // Restores the taskbar window to a normal layered state (alpha=255, no
        // WS_EX_TRANSPARENT) and clears the cached TaskbarHidden flag. Used both as
        // the "no autohide" branch and as a safety reset when switching from simple
        // mode (which uses alpha fade) to dynamic mode (which uses region clipping).
        private static void RestoreFullAlpha(Types.Taskbar tb)
        {
            int exStyle = LocalPInvoke.GetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            if ((exStyle & LocalPInvoke.WS_EX_LAYERED) != LocalPInvoke.WS_EX_LAYERED)
            {
                LocalPInvoke.SetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, exStyle | LocalPInvoke.WS_EX_LAYERED);
            }
            byte op = 0;
            bool ok = LocalPInvoke.GetLayeredWindowAttributes(tb.TaskbarHwnd, out _, out op, out _);
            if (!ok || op != 255)
            {
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
            }
            int style2 = LocalPInvoke.GetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            if ((style2 & LocalPInvoke.WS_EX_TRANSPARENT) == LocalPInvoke.WS_EX_TRANSPARENT)
            {
                LocalPInvoke.SetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, style2 ^ LocalPInvoke.WS_EX_TRANSPARENT);
            }
            tb.TaskbarHidden = false;
        }

        // Per-tick autohide processing for one taskbar. Branches on simple-vs-dynamic:
        //   - Simple: the existing whole-window alpha fade keyed off SimpleTaskbarLayout.AutoHide.
        //   - Dynamic: per-segment region clipping with linked hover groups (segments
        //     sharing the same AutoHide value share a hover-dwell timer and reveal/hide
        //     together; differing values are independent).
        private void ProcessAutohideTick(Types.Taskbar tb, Types.Settings settings, int current)
        {
            if (!settings.IsDynamic)
            {
                int simpleMode = settings.SimpleTaskbarLayout?.AutoHide ?? 0;
                // Keep all per-segment region flags true in simple mode — UpdateSimpleTaskbar
                // doesn't read them, but ApplyButton reuses these flags if the user later
                // switches into dynamic mode.
                tb.DynAppListVisible = true;
                tb.DynTrayVisible = true;
                tb.DynWidgetsVisible = true;
                tb.DynClockVisible = true;
                if (simpleMode > 0)
                {
                    ProcessSimpleAutohide(tb, settings, simpleMode, current);
                }
                else if (tb.TaskbarHidden || NeedsAlphaRestore(tb))
                {
                    RestoreFullAlpha(tb);
                    tb.Ignored = true;
                }
                return;
            }

            // Dynamic mode: alpha is unused, force it back to 255 (e.g. user just toggled
            // off Simple mode). We don't trigger Ignored here because that would force a
            // pointless region rebuild every tick — only the visibility flag transitions
            // below should request a rebuild.
            if (NeedsAlphaRestore(tb))
            {
                RestoreFullAlpha(tb);
            }

            int appListMode = settings.DynamicAppListLayout?.AutoHide ?? 0;
            int trayMode = settings.DynamicTrayLayout?.AutoHide ?? 0;
            int widgetsMode = settings.DynamicWidgetsLayout?.AutoHide ?? 0;
            int clockMode = settings.DynamicSecondaryClockLayout?.AutoHide ?? 0;

            // Quick exit when no segment in this mode wants to hide — keep all visible
            // and only rebuild the region if a previously-hidden flag is still cached.
            if (appListMode == 0 && trayMode == 0 && widgetsMode == 0 && clockMode == 0)
            {
                if (!tb.DynAppListVisible || !tb.DynTrayVisible || !tb.DynWidgetsVisible || !tb.DynClockVisible)
                {
                    tb.DynAppListVisible = true;
                    tb.DynTrayVisible = true;
                    tb.DynWidgetsVisible = true;
                    tb.DynClockVisible = true;
                    tb.Ignored = true;
                }
                tb.Group1Revealed = false;
                tb.Group2Revealed = false;
                tb.Group1HoverStartedAt = null;
                tb.Group1HoverEndedAt = null;
                tb.Group2HoverStartedAt = null;
                tb.Group2HoverEndedAt = null;
                return;
            }

            // Build hover state for each group as the union of its members' would-be
            // rects. Members that are currently hidden contribute the 2px bottom strip
            // instead of their full rect — that's how the user re-summons them.
            LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
            bool group1AnyHover = ComputeGroupHover(tb, settings, group: 1, appListMode, trayMode, widgetsMode, clockMode, msPt);
            bool group2AnyHover = ComputeGroupHover(tb, settings, group: 2, appListMode, trayMode, widgetsMode, clockMode, msPt);

            DateTime? g1Start = tb.Group1HoverStartedAt;
            DateTime? g1End = tb.Group1HoverEndedAt;
            tb.Group1Revealed = UpdateGroupReveal(tb, settings, group1AnyHover, ref g1Start, ref g1End, tb.Group1Revealed);
            tb.Group1HoverStartedAt = g1Start;
            tb.Group1HoverEndedAt = g1End;

            DateTime? g2Start = tb.Group2HoverStartedAt;
            DateTime? g2End = tb.Group2HoverEndedAt;
            tb.Group2Revealed = UpdateGroupReveal(tb, settings, group2AnyHover, ref g2Start, ref g2End, tb.Group2Revealed);
            tb.Group2HoverStartedAt = g2Start;
            tb.Group2HoverEndedAt = g2End;

            bool desktopForeground = (clockMode == 2 || trayMode == 2 || widgetsMode == 2 || appListMode == 2)
                                     && IsForegroundDesktop();

            bool wantAppList = SegmentShouldShow(appListMode, tb.Group1Revealed, tb.Group2Revealed, desktopForeground);
            bool wantTray = SegmentShouldShow(trayMode, tb.Group1Revealed, tb.Group2Revealed, desktopForeground);
            bool wantWidgets = SegmentShouldShow(widgetsMode, tb.Group1Revealed, tb.Group2Revealed, desktopForeground);
            bool wantClock = SegmentShouldShow(clockMode, tb.Group1Revealed, tb.Group2Revealed, desktopForeground);

            if (tb.DynAppListVisible != wantAppList || tb.DynTrayVisible != wantTray
                || tb.DynWidgetsVisible != wantWidgets || tb.DynClockVisible != wantClock)
            {
                tb.DynAppListVisible = wantAppList;
                tb.DynTrayVisible = wantTray;
                tb.DynWidgetsVisible = wantWidgets;
                tb.DynClockVisible = wantClock;
                tb.Ignored = true; // Force the refresh path further down to rebuild the region with the new visibility set.
#if DEBUG
                Log.Debug("Per-segment autohide on taskbar {Idx}: appList={A} tray={T} widgets={W} clock={C}", current, wantAppList, wantTray, wantWidgets, wantClock);
#endif
            }
        }

        private static bool SegmentShouldShow(int mode, bool group1Revealed, bool group2Revealed, bool desktopForeground)
        {
            return mode switch
            {
                0 => true,                                       // Always show
                1 => group1Revealed,                             // Always hide unless hover
                2 => desktopForeground || group2Revealed,        // Show on desktop, else hover
                _ => true,
            };
        }

        // True when ANY segment whose AutoHide value matches the requested group is
        // currently being hovered (or its 2px reveal strip, when the segment is hidden).
        private static bool ComputeGroupHover(Types.Taskbar tb, Types.Settings settings, int group, int appListMode, int trayMode, int widgetsMode, int clockMode, LocalPInvoke.POINT msPt)
        {
            bool any = false;
            if (appListMode == group) any |= HoverContains(tb, settings, 1, !tb.DynAppListVisible, msPt);
            if (trayMode == group) any |= HoverContains(tb, settings, 2, !tb.DynTrayVisible, msPt);
            if (widgetsMode == group) any |= HoverContains(tb, settings, 3, !tb.DynWidgetsVisible, msPt);
            if (clockMode == group) any |= HoverContains(tb, settings, 4, !tb.DynClockVisible, msPt);
            return any;
        }

        private static bool HoverContains(Types.Taskbar tb, Types.Settings settings, int segment, bool segmentHidden, LocalPInvoke.POINT msPt)
        {
            LocalPInvoke.RECT r = GetSegmentScreenRect(tb, settings, segment, segmentHidden);
            return LocalPInvoke.PtInRect(ref r, msPt);
        }

        private static bool NeedsAlphaRestore(Types.Taskbar tb)
        {
            byte op = 0;
            bool ok = LocalPInvoke.GetLayeredWindowAttributes(tb.TaskbarHwnd, out _, out op, out _);
            return !ok || op != 255;
        }

        // Legacy whole-window alpha fade preserved verbatim for simple mode. Identical
        // logic to the original v0.3.1 autohide block, rescoped from settings.AutoHide
        // to settings.SimpleTaskbarLayout.AutoHide (so the user's per-segment combobox
        // selection in simple mode still drives it).
        private void ProcessSimpleAutohide(Types.Taskbar tb, Types.Settings settings, int simpleMode, int current)
        {
            LocalPInvoke.RECT currentTaskbarRect = tb.TaskbarRect;
            LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
            bool isHoveringOverTaskbar;
            if (tb.TaskbarHidden)
            {
                currentTaskbarRect.Top = currentTaskbarRect.Bottom - 2;
                isHoveringOverTaskbar = LocalPInvoke.PtInRect(ref currentTaskbarRect, msPt);
            }
            else
            {
                isHoveringOverTaskbar = LocalPInvoke.PtInRect(ref currentTaskbarRect, msPt);
            }

            bool forceShow = false;
            if (simpleMode == 2 && IsForegroundDesktop())
            {
                forceShow = true;
            }

            double revealHoverMs = settings.HoverRevealDelayMs;
            bool hoverRevealReady = false;
            if (isHoveringOverTaskbar)
            {
                if (revealHoverMs <= 0)
                {
                    hoverRevealReady = true;
                }
                else if (tb.HoverStartedAt == null)
                {
                    tb.HoverStartedAt = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - tb.HoverStartedAt.Value).TotalMilliseconds >= revealHoverMs)
                {
                    hoverRevealReady = true;
                }
            }
            else
            {
                tb.HoverStartedAt = null;
            }

            double hideHoverMs = settings.HoverHideDelayMs;
            bool hoverHideReady = false;
            if (!isHoveringOverTaskbar)
            {
                if (hideHoverMs <= 0)
                {
                    hoverHideReady = true;
                }
                else if (tb.HoverEndedAt == null)
                {
                    tb.HoverEndedAt = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - tb.HoverEndedAt.Value).TotalMilliseconds >= hideHoverMs)
                {
                    hoverHideReady = true;
                }
            }
            else
            {
                tb.HoverEndedAt = null;
            }

            int animSpeed = 15;
            byte taskbarOpacity = 0;
            bool glwaOk = LocalPInvoke.GetLayeredWindowAttributes(tb.TaskbarHwnd, out _, out taskbarOpacity, out _);

#if DEBUG
            if (!_loggedAutohideEntered)
            {
                Log.Debug("Simple-mode autohide block active. SimpleAutoHide={Auto}", simpleMode);
                _loggedAutohideEntered = true;
            }
#endif

            int exStyle = LocalPInvoke.GetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
            bool layeredMissing = (exStyle & LocalPInvoke.WS_EX_LAYERED) != LocalPInvoke.WS_EX_LAYERED;
            if (layeredMissing)
            {
                LocalPInvoke.SetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, exStyle | LocalPInvoke.WS_EX_LAYERED);
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
                tb.TaskbarHidden = false;
#if DEBUG
                if (!_lastLoggedLayeredMissing.GetValueOrDefault(current))
                {
                    Log.Warning("Autohide: WS_EX_LAYERED was cleared on taskbar {Idx} (hwnd=0x{Hwnd:X}); re-applied and reset alpha to 255.", current, tb.TaskbarHwnd.ToInt64());
                    _lastLoggedLayeredMissing[current] = true;
                }
#endif
                return;
            }
#if DEBUG
            else if (_lastLoggedLayeredMissing.GetValueOrDefault(current))
            {
                Log.Information("Autohide: WS_EX_LAYERED stable again on taskbar {Idx}", current);
                _lastLoggedLayeredMissing[current] = false;
            }

            if (!glwaOk && !_lastLoggedGLWAFailed.GetValueOrDefault(current))
            {
                Log.Warning("Autohide: GetLayeredWindowAttributes returned false on taskbar {Idx} (hwnd=0x{Hwnd:X}) even though WS_EX_LAYERED is set. Hide/reveal branches will be skipped this tick.", current, tb.TaskbarHwnd.ToInt64());
                _lastLoggedGLWAFailed[current] = true;
            }
            else if (glwaOk && _lastLoggedGLWAFailed.GetValueOrDefault(current))
            {
                Log.Information("Autohide: GetLayeredWindowAttributes recovered on taskbar {Idx}, opacity={Opacity}", current, taskbarOpacity);
                _lastLoggedGLWAFailed[current] = false;
            }

            byte lastOp = _lastLoggedOpacity.GetValueOrDefault(current, (byte)255);
            bool opacityUnexpected = taskbarOpacity != 0 && taskbarOpacity != 1 && taskbarOpacity != 255
                                     && taskbarOpacity != 63 && taskbarOpacity != 127 && taskbarOpacity != 191;
            if (opacityUnexpected && taskbarOpacity != lastOp)
            {
                Log.Warning("Autohide: unexpected opacity {Opacity} on taskbar {Idx}", taskbarOpacity, current);
            }
            _lastLoggedOpacity[current] = taskbarOpacity;
#endif

            bool shouldBeVisible;
            if (taskbarOpacity == 1)
            {
                shouldBeVisible = hoverRevealReady || forceShow;
            }
            else
            {
                shouldBeVisible = isHoveringOverTaskbar || forceShow || !hoverHideReady;
            }

            if (shouldBeVisible && taskbarOpacity == 1)
            {
                int style = LocalPInvoke.GetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                if ((style & LocalPInvoke.WS_EX_TRANSPARENT) == LocalPInvoke.WS_EX_TRANSPARENT)
                {
                    LocalPInvoke.SetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_TRANSPARENT);
                }
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 63, LocalPInvoke.LWA_ALPHA);
                System.Threading.Thread.Sleep(animSpeed);
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 127, LocalPInvoke.LWA_ALPHA);
                System.Threading.Thread.Sleep(animSpeed);
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 191, LocalPInvoke.LWA_ALPHA);
                System.Threading.Thread.Sleep(animSpeed);
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 255, LocalPInvoke.LWA_ALPHA);
                tb.Ignored = true;
                tb.TaskbarHidden = false;
            }
            else if (!shouldBeVisible && taskbarOpacity == 255)
            {
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 191, LocalPInvoke.LWA_ALPHA);
                System.Threading.Thread.Sleep(animSpeed);
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 127, LocalPInvoke.LWA_ALPHA);
                System.Threading.Thread.Sleep(animSpeed);
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 63, LocalPInvoke.LWA_ALPHA);
                System.Threading.Thread.Sleep(animSpeed);
                LocalPInvoke.SetLayeredWindowAttributes(tb.TaskbarHwnd, 0, 1, LocalPInvoke.LWA_ALPHA);
                int style = LocalPInvoke.GetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32();
                if ((style & LocalPInvoke.WS_EX_TRANSPARENT) != LocalPInvoke.WS_EX_TRANSPARENT)
                {
                    LocalPInvoke.SetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE, LocalPInvoke.GetWindowLong(tb.TaskbarHwnd, LocalPInvoke.GWL_EXSTYLE).ToInt32() ^ LocalPInvoke.WS_EX_TRANSPARENT);
                }
                tb.Ignored = true;
                tb.TaskbarHidden = true;
            }
        }

        // Main method for the BackgroundWorker - runs indefinitely
        public void DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = sender as BackgroundWorker;
#if DEBUG
            Log.Information("Background worker started");
            int heartbeatCount = 0;
            DateTime lastHeartbeat = DateTime.UtcNow;
#endif
            while (true)
            {
#if DEBUG
                // Heartbeat every ~60s so if the worker hangs or dies we can see the
                // last timestamp. Debug-only so Release logs stay clean.
                heartbeatCount++;
                if (heartbeatCount >= 600)
                {
                    heartbeatCount = 0;
                    var now = DateTime.UtcNow;
                    Log.Debug("Background worker heartbeat (interval={Seconds:0.0}s)", (now - lastHeartbeat).TotalSeconds);
                    lastHeartbeat = now;
                }
#endif
                try
                {
                    if (worker.CancellationPending == true)
                    {
                        e.Cancel = true;
                        break;
                    }

                    // Primary loop for the running process
                    else
                    {
                        // Section for running less important things without requiring an additional thread
                        infrequentCount++;
                        if (infrequentCount == 10)
                        {
                            // Check to see if settings need to be shown
                            List<IntPtr> windowList = Interaction.GetTopLevelWindows();
                            foreach (IntPtr hwnd in windowList)
                            {
                                StringBuilder windowClass = new StringBuilder(1024);
                                StringBuilder windowTitle = new StringBuilder(1024);
                                try
                                {
                                    LocalPInvoke.GetClassName(hwnd, windowClass, 1024);
                                    LocalPInvoke.GetWindowText(hwnd, windowTitle, 1024);

                                    if (windowClass.ToString().Contains("HwndWrapper[RoundedTB.exe") && windowTitle.ToString() == "RoundedTB_SettingsRequest")
                                    {
                                        mw.Dispatcher.Invoke(() =>
                                        {
                                            if (mw.Visibility != Visibility.Visible)
                                            {
                                                mw.ShowMenuItem_Click(null, null);
                                            }
                                        });
                                        LocalPInvoke.SetWindowText(hwnd, "RoundedTB");
                                    }
                                }
                                catch (Exception) { }
                            }

                            // Force-reload the UIA TaskbarFrame every infrequent tick (~1s).
                            // Why: virtual-desktop switches (with "show windows only on the
                            // desktop they're open on" enabled) rebuild the taskbar's XAML
                            // children without changing any window handles. The cached
                            // _taskbarFrame element can then return a stale child snapshot,
                            // so the poll thinks AppListRect hasn't changed and the rounded
                            // region stays sized to the previous desktop's app count until
                            // Apply is pressed. Reloading unconditionally costs one cheap
                            // UIA query per taskbar per second.
                            mw.taskbarDetails?.ForEach(taskbar =>
                            {
                                taskbar.AppListXaml?.ReloadTaskbarFrameElement();
                            });

                            // Refresh TTB presence about once a second. This drives the
                            // gating in Taskbar.cs so we stop firing TTB_ForceRefreshTaskbar
                            // SendMessage calls into a non-existent worker window when TTB
                            // isn't running, and resumes them automatically once the user
                            // launches TTB without having to toggle CompositionCompat.
                            mw.activeSettings.TtbRunning = Interaction.IsTranslucentTBRunning();

                            infrequentCount = 0;
                        }

                        // Check if the taskbar is centred, and if it is, directly update the settings; using an interim bool to avoid delaying because I'm lazy
                        bool isCentred = Taskbar.CheckIfCentred();
                        mw.activeSettings.IsCentred = isCentred;

                        // Work with static values to avoid some null reference exceptions
                        List<Types.Taskbar> taskbars = mw.taskbarDetails;
                        Types.Settings settings = mw.activeSettings;

                        // If the number of taskbars has changed, regenerate taskbar information
                        if (Taskbar.TaskbarCountOrHandleChanged(taskbars.Count, taskbars[0].TaskbarHwnd))
                        {
                            // Forcefully reset taskbars if the taskbar count or main taskbar handle has changed
                            taskbars.ForEach(t => t.Dispose());
                            taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                            Debug.WriteLine("Regenerating taskbar info");
                        }

                        for (int current = 0; current < taskbars.Count; current++)
                        {
                            if (taskbars[current].TaskbarHwnd == IntPtr.Zero || taskbars[current].AppListHwnd == IntPtr.Zero)
                            {
                                taskbars.ForEach(t => t.Dispose());
                                taskbars = Taskbar.GenerateTaskbarInfo(mw.interaction.IsWindows11());
                                Debug.WriteLine("Regenerating taskbar info due to a missing handle");
                                break;
                            }
                            // Get the latest quick details of this taskbar
                            Types.Taskbar newTaskbar = Taskbar.GetQuickTaskbarRects(taskbars[current].TaskbarHwnd, taskbars[current].TrayHwnd, taskbars[current].AppListHwnd, taskbars[current].AppListXaml);


                            // If the taskbar's monitor has a maximised window, reset it so it's "filled"
                            if (Taskbar.TaskbarShouldBeFilled(taskbars[current].TaskbarHwnd, settings))
                            {
                                if (taskbars[current].Ignored == false)
                                {
                                    Taskbar.ResetTaskbar(taskbars[current], settings);
                                    taskbars[current].Ignored = true;
                                }
                                continue;
                            }

                            // Showhide tray on hover
                            if (settings.ShowSegmentsOnHover)
                            {
                                LocalPInvoke.RECT currentTrayRect = taskbars[current].TrayRect;
                                LocalPInvoke.RECT currentWidgetsRect = taskbars[current].TaskbarRect;
                                currentWidgetsRect.Right = Convert.ToInt32(currentWidgetsRect.Right - (currentWidgetsRect.Right - currentWidgetsRect.Left) + (168 * taskbars[current].ScaleFactor));

                                if (currentTrayRect.Left != 0)
                                {
                                    LocalPInvoke.GetCursorPos(out LocalPInvoke.POINT msPt);
                                    bool isHoveringOverTray = LocalPInvoke.PtInRect(ref currentTrayRect, msPt);
                                    bool isHoveringOverWidgets = LocalPInvoke.PtInRect(ref currentWidgetsRect, msPt);
                                    if (isHoveringOverTray && !settings.ShowTray)
                                    {
                                        settings.ShowTray = true;
                                        taskbars[current].Ignored = true;
                                    }
                                    else if (!isHoveringOverTray)
                                    {
                                        taskbars[current].Ignored = true;
                                        settings.ShowTray = false;
                                    }

                                    if (isHoveringOverWidgets && !settings.ShowWidgets)
                                    {
                                        settings.ShowWidgets = true;
                                        taskbars[current].Ignored = true;
                                    }
                                    else if (!isHoveringOverWidgets)
                                    {
                                        taskbars[current].Ignored = true;
                                        settings.ShowWidgets = false;
                                    }
                                }
                            }

                            // Per-segment autohide in dynamic mode is implemented by
                            // omitting hidden segments from the SetWindowRgn region;
                            // simple mode keeps the legacy whole-window alpha fade.
                            // Both still need the AlwaysOnTop / work-area side effects
                            // applied once via MainWindow.AutoHide(true), but visibility
                            // mechanics differ enough to live in dedicated helpers.
                            ProcessAutohideTick(taskbars[current], settings, current);


                            // If the taskbar's overall rect has changed, update it. If it's simple, just update. If it's dynamic, check it's a valid change, then update it.
                            if (Taskbar.TaskbarRefreshRequired(taskbars[current], newTaskbar, settings.IsDynamic) || taskbars[current].Ignored || redrawOverride)
                            {
                                Debug.WriteLine($"Refresh required on taskbar {current}");
                                taskbars[current].Ignored = false;
                                int isFullTest = newTaskbar.TrayRect.Left - newTaskbar.AppListRect.Right;
                                if (!settings.IsDynamic || (isFullTest <= taskbars[current].ScaleFactor * 25 && isFullTest > 0 && newTaskbar.TrayRect.Left != 0))
                                {
                                    // Add the rect changes to the temporary list of taskbars
                                    taskbars[current].TaskbarRect = newTaskbar.TaskbarRect;
                                    taskbars[current].AppListRect = newTaskbar.AppListRect;
                                    taskbars[current].TrayRect = newTaskbar.TrayRect;
                                    Taskbar.UpdateSimpleTaskbar(taskbars[current], settings);
                                }
                                else
                                {
                                    if (Taskbar.CheckDynamicUpdateIsValid(taskbars[current], newTaskbar))
                                    {
                                        // Add the rect changes to the temporary list of taskbars
                                        taskbars[current].TaskbarRect = newTaskbar.TaskbarRect;
                                        taskbars[current].AppListRect = newTaskbar.AppListRect;
                                        taskbars[current].TrayRect = newTaskbar.TrayRect;
                                        Taskbar.UpdateDynamicTaskbar(taskbars[current], settings);
                                    }
                                }
                            }
                        }
                        mw.taskbarDetails = taskbars;


                        System.Threading.Thread.Sleep(100);
                    }
                }
                catch (Exception ex)
                {
                    // Worker used to die silently on anything except TypeInitializationException.
                    // Log and continue so autohide keeps running; if the same error repeats every
                    // tick it will be obvious in the log.
                    Log.Error(ex, "Background worker iteration threw {Type}", ex.GetType().Name);
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }
}
