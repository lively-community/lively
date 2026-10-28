using Lively.Common;
using Lively.Common.Com;
using Lively.Common.Exceptions;
using Lively.Common.Extensions;
using Lively.Common.Factories;
using Lively.Common.Helpers;
using Lively.Common.Helpers.Pinvoke;
using Lively.Common.Helpers.Shell;
using Lively.Common.Services;
using Lively.Core.Display;
using Lively.Core.Suspend;
using Lively.Core.Wallpapers;
using Lively.Core.Watchdog;
using Lively.Extensions;
using Lively.Factories;
using Lively.Helpers;
using Lively.Models;
using Lively.Models.Enums;
using Lively.Models.Message;
using Lively.Views.WindowMsg;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinEventHook;
using Timer = System.Timers.Timer;

namespace Lively.Core
{
    public class WinDesktopCore : IDesktopCore
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly SemaphoreSlim semaphoreSlimWallpaperLoadingLock = new(1, 1);
        private readonly List<IWallpaper> wallpapers = new(2);
        public ReadOnlyCollection<IWallpaper> Wallpapers => wallpapers.AsReadOnly();
        private IntPtr workerW, progman, shellDLL_DefView, original_WorkerW;
        public IntPtr DesktopWorkerW => workerW;
        private bool disposedValue;
        private bool isRaisedDesktopWithLayeredShellView;
        /// <summary>
        /// Set wallpaper as child of WorkerW.
        /// </summary>
        private readonly bool isWorkerWParent;
        private readonly List<WallpaperLayoutModel> wallpapersDisconnected = [];

        private int prevExplorerPid = GetTaskbarExplorerPid();
        private DateTime prevExplorerCrashTime = DateTime.MinValue;

        public event EventHandler<Exception> WallpaperError;
        public event EventHandler WallpaperChanged;
        public event EventHandler WallpaperReset;

        // Screensaver
        private readonly Timer screensaverIdleTimer = new();
        private readonly uint screensaverIdleCheckTime = 30000;
        private uint screensaverIdleWaitTime = 300000;

        private readonly IUserSettingsService userSettings;
        private readonly IWallpaperPluginFactory wallpaperFactory;
        private readonly IWallpaperLibraryFactory wallpaperLibraryFactory;
        private readonly ITransparentTbService ttbService;
        private readonly IWatchdogService watchdog;
        private readonly IPlayback playback;
        private readonly IScreensaverService screensaver;
        private readonly RawInputMsgWindow rawInput;
        private readonly WndProcMsgWindow WndProc;
        private readonly IDisplayManager displayManager;
        private readonly WindowEventHook workerWHook;

        public WinDesktopCore(IUserSettingsService userSettings,
            IDisplayManager displayManager,
            ITransparentTbService ttbService,
            IPlayback playback,
            IScreensaverService screensaver,
            IWatchdogService watchdog,
            RawInputMsgWindow rawInput,
            WndProcMsgWindow wndProc,
            //IScreensaverService screensaver,
            IWallpaperPluginFactory wallpaperFactory,
            IWallpaperLibraryFactory wallpaperLibraryFactory)
        {
            this.userSettings = userSettings;
            this.displayManager = displayManager;
            this.ttbService = ttbService;
            this.watchdog = watchdog;
            this.playback = playback;
            this.rawInput = rawInput;
            this.WndProc = wndProc;
            this.screensaver = screensaver;
            this.wallpaperFactory = wallpaperFactory;
            this.wallpaperLibraryFactory = wallpaperLibraryFactory;

            if (App.IsExclusiveScreensaverMode)
                return;

            if (SystemParameters.HighContrast)
                Logger.Warn("Highcontrast mode detected, some functionalities may not work properly.");

            // Set user preference for wallpaper layering.
            isWorkerWParent = userSettings.Settings.IsWorkerWParent;

            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            this.displayManager.DisplayUpdated += DisplaySettingsChanged_Hwnd;
            this.WndProc.TaskbarCreated += WndProc_TaskbarCreated;
            this.WallpaperChanged += SetupDesktop_WallpaperChanged;
            this.playback.WallpaperControlChanged += Playback_WallpaperControlChanged;
            this.rawInput.MouseMoveRaw += RawInput_MouseMoveRaw;
            this.rawInput.MouseDownRaw += RawInput_MouseDownRaw;
            this.rawInput.MouseUpRaw += RawInput_MouseUpRaw;
            this.rawInput.MouseWheelRaw += RawInput_MouseWheelRaw;
            this.rawInput.KeyboardClickRaw += RawInput_KeyboardClickRaw;
            this.screensaverIdleTimer.Elapsed += ScreensaverIdleTimer_Elapsed;

            // Initialize desktop and update handles.
            SetupDesktopLayer();

            // Some startup races leave the raw input sink deaf until a re-register;
            // heal once shortly after boot so wheel forwarding survives bad starts.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15));
                    this.rawInput.Dispatcher.Invoke(() =>
                    {
                        this.rawInput.ReRegisterRawInputDevices();
                        Logger.Info("Raw input devices re-registered (startup heal).");
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            });

            try
            {
                if (workerW != IntPtr.Zero)
                {
                    Logger.Info("Hooking WorkerW events..");
                    var dwThreadId = NativeMethods.GetWindowThreadProcessId(workerW, out int dwProcessId);
                    workerWHook = new WindowEventHook(WindowEvent.EVENT_OBJECT_DESTROY);
                    workerWHook.HookToThread(dwThreadId);
                    workerWHook.EventReceived += WorkerWHook_EventReceived;
                }
                else
                {
                    Logger.Error("Failed to initialize Core, WorkerW is NULL");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"WorkerW hook failed: {ex.Message}");
            }

            // Screensaver
            screensaverIdleTimer.Interval = screensaverIdleCheckTime;
            if (userSettings.Settings.ScreensaverIdleDelay != ScreensaverIdleTime.none)
                StartScreensaverIdleTimer(userSettings.Settings.ScreensaverIdleDelay.ToMilliseconds());
        }

        private void SetupDesktopLayer()
        {
            Logger.Info($"Initializing WorkerW");

            // Update program manager
            progman = DesktopUtil.GetProgman();

            /*
            Microsoft:
            As Windows evolves to help deliver the best customer experiences, we have changed how the 
            Background renders in Windows to enable new scenarios such as HDR Backgrounds as announced 
            in Windows Insider.

            When the desktop is split out from the list view window (aka the "raised desktop") we no 
            longer create multiple top-level HWNDs to support this scenario. Instead, the top-level 
            "Progman" window is now created with WS_EX_NOREDIRECTIONBITMAP (so there is no GDI content 
            for that window at all) and the shell DefView child window is a WS_EX_LAYERED child window. 
            When the desktop is raised, we create a child WorkerW window that is z-ordered under the 
            DefView that will render the wallpaper. The DefView window will draw mostly transparent with 
            just the icons and text.

            If your application forces the "raised desktop" state, it will now need to create its own 
            WS_EX_LAYERED child HWND that is z-ordered under the DefView window but above the WorkerW 
            window. This window should likely be a SetLayeredWindowAttributes(bAlpha=0xFF) window so 
            that you can do DX blt presents to it and not suffer performance issues.
            */
            isRaisedDesktopWithLayeredShellView = WindowUtil.HasExtendedStyle(progman, NativeMethods.WindowStyles.WS_EX_NOREDIRECTIONBITMAP);
            if (isRaisedDesktopWithLayeredShellView)
                Logger.Info($"Raised desktop with layered ShellView detected.");

            // Send 0x052C to Progman. This message directs Progman to spawn a 
            // WorkerW behind the desktop icons. If it is already there, nothing 
            // happens.
            NativeMethods.SendMessageTimeout(progman,
                                   0x052C,
                                   new IntPtr(0xD),
                                   new IntPtr(0x1),
                                   NativeMethods.SendMessageTimeoutFlags.SMTO_NORMAL,
                                   1000,
                                   out _);

            // Spy++ output
            // .....
            // 0x00010190 "" WorkerW
            //   ...
            //   0x000100EE "" SHELLDLL_DefView
            //     0x000100F0 "FolderView" SysListView32
            // 0x00100B8A "" WorkerW       <-- This is the WorkerW instance we are after!
            // 0x000100EC "Program Manager" Progman
            // We enumerate all Windows, until we find one, that has the SHELLDLL_DefView 
            // as a child. 
            // If we found that window, we take its next sibling and assign it to workerw.
            NativeMethods.EnumWindows(new NativeMethods.EnumWindowsProc((tophandle, topparamhandle) =>
            {
                IntPtr p = NativeMethods.FindWindowEx(tophandle,
                                            IntPtr.Zero,
                                            "SHELLDLL_DefView",
                                            IntPtr.Zero);

                if (p != IntPtr.Zero)
                {
                    // Gets the WorkerW Window after the current one.
                    workerW = NativeMethods.FindWindowEx(IntPtr.Zero,
                                                    tophandle,
                                                    "WorkerW",
                                                    IntPtr.Zero);
                    shellDLL_DefView = p;
                }

                return true;
            }), IntPtr.Zero);

            if (isRaisedDesktopWithLayeredShellView)
            {
                // Spy++ output
                // 0x000100EC "Program Manager" Progman
                //   0x000100EE "" SHELLDLL_DefView
                //     0x000100F0 "FolderView" SysListView32
                //   0x00100B8A "" WorkerW       <-- This is the WorkerW instance we are after!
                workerW = NativeMethods.FindWindowEx(progman,
                                                IntPtr.Zero,
                                                "WorkerW",
                                                IntPtr.Zero);
            }

            if (IsWindows7)
            {
                // This should fix the wallpaper disappearing issue.
                if (!workerW.Equals(progman))
                    NativeMethods.ShowWindow(workerW, (uint)0);

                // WorkerW is assumed as progman here.
                workerW = progman;
            }

            // For checking if desktop foreground.
            original_WorkerW = DesktopUtil.GetDesktopWorkerW();

            Logger.Info($"WorkerW initialized {workerW}");
            WallpaperReset?.Invoke(this, EventArgs.Empty);
        }

        private async void WorkerWHook_EventReceived(object sender, WinEventHookEventArgs e)
        {
            if (e.WindowHandle != workerW || e.EventType != WindowEvent.EVENT_OBJECT_DESTROY)
                return;

            // Should we verify the thread of new workerW and re-attach the hook?
            Logger.Error("WorkerW destroyed.");
            if (isRaisedDesktopWithLayeredShellView && !isWorkerWParent)
            {
                SetupDesktopLayer();

                var windowFlags = (int)(NativeMethods.SetWindowPosFlags.SWP_NOMOVE |
                    NativeMethods.SetWindowPosFlags.SWP_NOSIZE |
                    NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE);

                foreach (var item in wallpapers)
                {
                    NativeMethods.SetWindowPos(item.Handle,
                        (int)shellDLL_DefView,
                        0,
                        0,
                        0,
                        0,
                        windowFlags);
                }
                EnsureWorkerWZOrder();
            }
            else
            {
                await ResetWallpaperAsync();
            }
        }

        /// <summary>
        /// Sets the given wallpaper based on layout usersettings.
        /// </summary>
        public async Task SetWallpaperAsync(LibraryModel wallpaper, DisplayMonitor display)
        {
            await semaphoreSlimWallpaperLoadingLock.WaitAsync();

            try
            {
                Logger.Info($"Setting wallpaper: {wallpaper.Title} | {wallpaper.FilePath}");

                // Verify file exists if outside wallpaper install folder
                var fileExists = !wallpaper.LivelyInfo.IsAbsolutePath || wallpaper.LivelyInfo.Type.IsOnlineWallpaper() || File.Exists(wallpaper.FilePath);
                if (!fileExists)
                {
                    Logger.Info($"Skipping wallpaper, file {wallpaper.LivelyInfo.FileName} not found.");
                    WallpaperError?.Invoke(this, new WallpaperNotFoundException($"{Properties.Resources.TextFileNotFound}\n{wallpaper.LivelyInfo.FileName}"));
                    WallpaperChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }

                if (!watchdog.IsRunning)
                    watchdog.Start();

                IWallpaper currentWallpaper = null;
                try
                {
                    switch (userSettings.Settings.WallpaperArrangement)
                    {
                        case WallpaperArrangement.per:
                            {
                                // WebView2 can share a rendering process across multiple instances based on the UserData folder.
                                // This sometimes causes the rendering process to start in a paused state because of current wallpaper (?),
                                // leading to wallpaper startup issues. We are closing first for safety.
                                // Refer to IWebView2UserDataFactory for more details.
                                CloseWallpaper(display, fireEvent: false);

                                currentWallpaper = wallpaperFactory.CreateWallpaper(wallpaper, display, userSettings.Settings.WallpaperArrangement);
                                currentWallpaper.Exited += Wallpaper_Exited;
                                currentWallpaper.Loaded += Wallpaper_Loaded;
                                await currentWallpaper.ShowAsync();

                                if (!TrySetWallpaperPerScreen(currentWallpaper.Handle, currentWallpaper.Screen))
                                    Logger.Error("Failed to set wallpaper as child of WorkerW");

                                // Reload incase page does not handle resize event
                                if (currentWallpaper.Category.IsWebWallpaper())
                                    currentWallpaper.SetPlaybackPos(0, PlaybackPosType.absolutePercent);

                                if (currentWallpaper.Pid is int pid)
                                    watchdog.Add(pid);

                                wallpapers.Add(currentWallpaper);
                            }
                            break;
                        case WallpaperArrangement.span:
                            {
                                CloseAllWallpapers(fireEvent: false);

                                currentWallpaper = wallpaperFactory.CreateWallpaper(wallpaper, display, userSettings.Settings.WallpaperArrangement);
                                currentWallpaper.Exited += Wallpaper_Exited;
                                currentWallpaper.Loaded += Wallpaper_Loaded;
                                await currentWallpaper.ShowAsync();

                                if (!TrySetWallpaperSpanScreen(currentWallpaper.Handle))
                                    Logger.Error("Failed to set wallpaper as child of WorkerW");

                                if (currentWallpaper.Category.IsWebWallpaper())
                                    currentWallpaper.SetPlaybackPos(0, PlaybackPosType.absolutePercent);

                                if (currentWallpaper.Pid is int pid)
                                    watchdog.Add(pid);

                                wallpapers.Add(currentWallpaper);
                            }
                            break;
                        case WallpaperArrangement.duplicate:
                            {
                                CloseAllWallpapers(false);

                                foreach (var item in displayManager.DisplayMonitors)
                                {
                                    currentWallpaper = wallpaperFactory.CreateWallpaper(wallpaper, item, userSettings.Settings.WallpaperArrangement);
                                    currentWallpaper.Exited += Wallpaper_Exited;
                                    currentWallpaper.Loaded += Wallpaper_Loaded;
                                    await currentWallpaper.ShowAsync();

                                    if (!TrySetWallpaperPerScreen(currentWallpaper.Handle, currentWallpaper.Screen))
                                        Logger.Error("Failed to set wallpaper as child of WorkerW");

                                    if (currentWallpaper.Category.IsWebWallpaper())
                                        currentWallpaper.SetPlaybackPos(0, PlaybackPosType.absolutePercent);

                                    if (currentWallpaper.Pid is int pid)
                                        watchdog.Add(pid);

                                    wallpapers.Add(currentWallpaper);
                                }

                                // Synchronizing position and audio
                                foreach (var item in wallpapers)
                                {
                                    if (!item.Screen.IsPrimary)
                                    {
                                        Logger.Info($"Disabling audio track on screen {item.Screen.DeviceName} (duplicate.)");
                                        item.SetMute(true);
                                    }
                                    item.SetPlaybackPos(0, PlaybackPosType.absolutePercent);
                                }
                            }
                            break;
                    }
                    WallpaperChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (WallpaperPluginFactory.MsixNotAllowedException ex1)
                {
                    Logger.Error(ex1);
                    WallpaperError?.Invoke(this, new WallpaperPluginNotFoundException(ex1.Message));
                    WallpaperChanged?.Invoke(this, EventArgs.Empty);

                    currentWallpaper?.Dispose();
                }
                catch (Win32Exception ex2)
                {
                    Logger.Error(ex2);
                    if (ex2.NativeErrorCode == 2) //ERROR_FILE_NOT_FOUND
                        WallpaperError?.Invoke(this, new WallpaperPluginNotFoundException(ex2.Message));
                    else
                        WallpaperError?.Invoke(this, ex2);
                    WallpaperChanged?.Invoke(this, EventArgs.Empty);

                    currentWallpaper?.Dispose();
                }
                catch (Exception ex3)
                {
                    Logger.Error(ex3);
                    WallpaperError?.Invoke(this, ex3);
                    WallpaperChanged?.Invoke(this, EventArgs.Empty);

                    currentWallpaper?.Dispose();
                }
            }
            finally
            {
                semaphoreSlimWallpaperLoadingLock.Release();
            }
        }

        private async void Wallpaper_Loaded(object sender, EventArgs e)
        {
            await SetDesktopPictureOrLockscreen(sender as IWallpaper);
        }

        private void Wallpaper_Exited(object sender, EventArgs e)
        {
            RefreshDesktop();
        }

        private async Task SetDesktopPictureOrLockscreen(IWallpaper wallpaper)
        {
            //Only consider PrimaryScreen for calculating average color
            var thumbRequiredAvgColor = (userSettings.Settings.SystemTaskbarTheme == TaskbarTheme.wallpaper || userSettings.Settings.SystemTaskbarTheme == TaskbarTheme.wallpaperFluent)
                && (!displayManager.IsMultiScreen() || userSettings.Settings.WallpaperArrangement == WallpaperArrangement.span || wallpaper.Screen.IsPrimary);
            if (userSettings.Settings.DesktopAutoWallpaper || thumbRequiredAvgColor)
            {
                try
                {
                    // Desktop-bridge redirection not working IDesktopWallpaper COM/SystemParametersInfo.
                    var resolvedScreenshotDir = PackageUtil.ValidateAndResolvePath(Constants.CommonPaths.ScreenshotDir);
                    var imgPath = Path.Combine(resolvedScreenshotDir, Path.GetRandomFileName() + ".jpg");
                    await wallpaper.ScreenCapture(imgPath);

                    //set accent color of taskbar..
                    if (thumbRequiredAvgColor)
                    {
                        try
                        {
                            var color = await Task.Run(() => ttbService.GetAverageColor(imgPath));
                            ttbService.SetAccentColor(color);
                        }
                        catch (Exception ie1)
                        {
                            Logger.Error($"Failed to set taskbar accent: {ie1.Message}");
                        }
                    }

                    //set desktop picture wallpaper..
                    if (userSettings.Settings.DesktopAutoWallpaper)
                    {
                        if (true)//displayManager.IsMultiScreen())
                        {
                            //Has transition animation..
                            var desktop = (IDesktopWallpaper)new DesktopWallpaperClass();
                            DesktopWallpaperPosition scaler = DesktopWallpaperPosition.Fill;
                            switch (userSettings.Settings.WallpaperScaling)
                            {
                                case WallpaperScaler.none:
                                    scaler = DesktopWallpaperPosition.Center;
                                    break;
                                case WallpaperScaler.fill:
                                    scaler = DesktopWallpaperPosition.Stretch;
                                    break;
                                case WallpaperScaler.uniform:
                                    scaler = DesktopWallpaperPosition.Fit;
                                    break;
                                case WallpaperScaler.uniformFill:
                                    //not exaclty the same, lively's uniform fill pivot is topleft whereas for windows its center.
                                    scaler = DesktopWallpaperPosition.Fill;
                                    break;
                            }
                            desktop.SetPosition(userSettings.Settings.WallpaperArrangement == WallpaperArrangement.span ? DesktopWallpaperPosition.Span : scaler);
                            desktop.SetWallpaper(userSettings.Settings.WallpaperArrangement == WallpaperArrangement.span ? null : wallpaper.Screen.DeviceId, imgPath);
                        }
                        else
                        {
                            //No transition animation..
                            _ = NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETDESKWALLPAPER,
                                0,
                                imgPath,
                                NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDWININICHANGE);
                        }
                    }
                }
                catch (Exception ie2)
                {
                    Logger.Error($"Failed to set lockscreen/desktop wallpaper: {ie2.Message}");
                }
            }
        }

        /// <summary>
        /// Calculates the position of window w.r.t parent workerw handle & sets it as child window to it.
        /// </summary>
        /// <param name="handle">window handle of process to add as wallpaper</param>
        /// <param name="display">displaystring of display to sent wp to.</param>
        private bool TrySetWallpaperPerScreen(IntPtr handle, DisplayMonitor targetDisplay)
        {
            Logger.Info($"Sending wallpaper(Screen): {targetDisplay.DeviceName} | {targetDisplay.Bounds}");

            var prct = new NativeMethods.RECT();
            //Position the wp fullscreen to corresponding display.
            if (!NativeMethods.SetWindowPos(
                handle,
                1,
                targetDisplay.Bounds.X,
                targetDisplay.Bounds.Y,
                (targetDisplay.Bounds.Width),
                (targetDisplay.Bounds.Height),
                (int)NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE))
            {
                Logger.Info(LogUtil.GetWin32Error("Failed to set perscreen wallpaper(1)"));
            }

            NativeMethods.MapWindowPoints(handle, workerW, ref prct, 2);
            var success = TryAttachToDesktop(handle);

            //Position the wp window relative to the new parent window(workerw).
            if (!NativeMethods.SetWindowPos(handle,
                1,
                prct.Left,
                prct.Top,
                (targetDisplay.Bounds.Width),
                (targetDisplay.Bounds.Height),
                (int)(NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE | NativeMethods.SetWindowPosFlags.SWP_NOZORDER)))
            {
                Logger.Info(LogUtil.GetWin32Error("Failed to set perscreen wallpaper(2)"));
            }
            RefreshDesktop();
            return success;
        }

        /// <summary>
        /// Spans wp across all screens.
        /// </summary>
        private bool TrySetWallpaperSpanScreen(IntPtr handle)
        {
            //get spawned workerw rectangle data.
            NativeMethods.GetWindowRect(workerW, out NativeMethods.RECT prct);
            var success = TryAttachToDesktop(handle);

            //fill wp into the whole workerw area.
            Logger.Info($"Sending wallpaper(Span): ({prct.Left}, {prct.Top}, {prct.Right - prct.Left}, {prct.Bottom - prct.Top}).");
            if (!NativeMethods.SetWindowPos(handle,
                                            1,
                                            0,
                                            0,
                                            prct.Right - prct.Left,
                                            prct.Bottom - prct.Top,
                                            (int)(NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE | NativeMethods.SetWindowPosFlags.SWP_NOZORDER)))
            {
                Logger.Info(LogUtil.GetWin32Error("Failed to set span wallpaper"));
            }
            RefreshDesktop();
            return success;
        }

        /// <summary>
        /// Reset workerw.
        /// </summary>
        public async Task ResetWallpaperAsync()
        {
            await semaphoreSlimWallpaperLoadingLock.WaitAsync();

            try
            {
                Logger.Info("Restarting wallpaper service..");
                // Copy existing wallpapers
                var originalWallpapers = Wallpapers.ToList();
                CloseAllWallpapers(false);
                // Restart workerw
                SetupDesktopLayer();
                if (workerW == IntPtr.Zero)
                {
                    Logger.Info("Retry creating WorkerW after delay..");
                    await Task.Delay(500);
                    SetupDesktopLayer();
                }
                foreach (var item in originalWallpapers)
                {
                    _ = SetWallpaperAsync(item.Model, item.Screen);
                    if (userSettings.Settings.WallpaperArrangement == WallpaperArrangement.duplicate)
                        break;
                }
            }
            finally
            {
                semaphoreSlimWallpaperLoadingLock.Release();
            }
        }

        public async Task RestartWallpaper()
        {
            // Copy existing wallpapers
            var originalWallpapers = Wallpapers.ToList();
            CloseAllWallpapers(false);
            foreach (var item in originalWallpapers)
            {
                await SetWallpaperAsync(item.Model, item.Screen);
                if (userSettings.Settings.WallpaperArrangement == WallpaperArrangement.duplicate)
                    break;
            }

        }

        public async Task RestartWallpaper(DisplayMonitor display)
        {
            // Copy existing wallpapers
            var originalWallpapers = Wallpapers.Where(x => x.Screen.Equals(display)).ToList();
            CloseWallpaper(display, false);
            foreach (var item in originalWallpapers)
            {
                await SetWallpaperAsync(item.Model, item.Screen);
                if (userSettings.Settings.WallpaperArrangement == WallpaperArrangement.duplicate)
                    break;
            }

        }

        private void SetupDesktop_WallpaperChanged(object sender, EventArgs e)
        {
            SaveWallpaperLayout();
        }

        readonly object layoutWriteLock = new object();
        private void SaveWallpaperLayout()
        {
            lock (layoutWriteLock)
            {
                userSettings.WallpaperLayout.Clear();
                wallpapers.ForEach(wallpaper =>
                {
                    userSettings.WallpaperLayout.Add(new WallpaperLayoutModel(
                            (DisplayMonitor)wallpaper.Screen,
                            wallpaper.Model.LivelyInfoFolderPath));
                });
                if (userSettings.Settings.WallpaperArrangement == WallpaperArrangement.per)
                {
                    userSettings.WallpaperLayout.AddRange(wallpapersDisconnected);
                }
                /*
                layout.AddRange(wallpapersDisconnected.Except(wallpapersDisconnected.FindAll(
                    layout => Wallpapers.FirstOrDefault(wp => ScreenHelper.ScreenCompare(layout.LivelyScreen, wp.GetScreen(), DisplayIdentificationMode.deviceId)) != null)));
                */
                try
                {
                    userSettings.Save<List<WallpaperLayoutModel>>();
                }
                catch (Exception e)
                {
                    Logger.Error(e.ToString());
                }
            }
        }

        private async void DisplaySettingsChanged_Hwnd(object sender, EventArgs e)
        {
            // SetWallpaperAsync() is called here but not awaited so should be fine.
            // Only possible case of deadlock is if both methods gets executed simulataneously which is unlikely.
            // If required add a timeout to WaitAsync() for all the semaphore calls.
            await semaphoreSlimWallpaperLoadingLock.WaitAsync();
            try
            {
                using (playback.DeferPlayback())
                {
                    Logger.Info("Display settings changed, screen(s):");
                    displayManager.DisplayMonitors.ToList().ForEach(x => Logger.Info(x.DeviceName + " " + x.Bounds));
                    RefreshWallpaper();
                    RestoreDisconnectedWallpapers();
                    EnsureWorkerWZOrder();
                }
            }
            finally
            {
                semaphoreSlimWallpaperLoadingLock.Release();
            }
        }

        private void RefreshWallpaper()
        {
            try
            {
                //Wallpapers still running on disconnected screens.
                var allScreens = displayManager.DisplayMonitors.ToList();
                var orphanWallpapers = wallpapers.FindAll(
                    wallpaper => allScreens.Find(
                        screen => wallpaper.Screen.Equals(screen)) == null);

                //Updating user selected screen to primary if disconnected.
                userSettings.Settings.SelectedDisplay =
                    allScreens.Find(x => userSettings.Settings.SelectedDisplay.Equals(x)) ??
                    displayManager.PrimaryDisplayMonitor;
                userSettings.Save<SettingsModel>();

                switch (userSettings.Settings.WallpaperArrangement)
                {
                    case WallpaperArrangement.per:
                        //No screens running wallpaper needs to be removed.
                        if (orphanWallpapers.Count != 0)
                        {
                            var newOrphans = orphanWallpapers.FindAll(
                                oldOrphan => wallpapersDisconnected.Find(
                                    newOrphan => newOrphan.Display.Equals(oldOrphan.Screen)) == null);
                            foreach (var item in newOrphans)
                            {
                                wallpapersDisconnected.Add(new WallpaperLayoutModel(item.Screen, item.Model.LivelyInfoFolderPath));
                            }
                            orphanWallpapers.ForEach(x =>
                            {
                                Logger.Info($"Disconnected Screen: {x.Screen.DeviceName} {x.Screen.Bounds}");
                                x.Dispose();
                            });
                            wallpapers.RemoveAll(x => orphanWallpapers.Contains(x));
                        }
                        break;
                    case WallpaperArrangement.duplicate:
                        if (orphanWallpapers.Count != 0)
                        {
                            orphanWallpapers.ForEach(x =>
                            {
                                Logger.Info($"Disconnected Screen: {x.Screen.DeviceName} {x.Screen.Bounds}");
                                x.Dispose();
                            });
                            wallpapers.RemoveAll(x => orphanWallpapers.Contains(x));
                        }
                        break;
                    case WallpaperArrangement.span:
                        //Only update wallpaper rect.
                        break;
                }
                //Desktop size change when screen is added/removed/property changed.
                UpdateWallpaperRect();
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
            }
            finally
            {
                //Notifying display/wallpaper change.
                WallpaperChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void UpdateWallpaperRect()
        {
            if (displayManager.IsMultiScreen() && userSettings.Settings.WallpaperArrangement == WallpaperArrangement.span)
            {
                if (wallpapers.Count != 0)
                {
                    //Wallpapers[0].Play();
                    var screenArea = displayManager.VirtualScreenBounds;
                    Logger.Info($"Updating wallpaper rect(Span): ({screenArea.Width}, {screenArea.Height}).");
                    //For play/pause, setting the new metadata.
                    Wallpapers[0].Screen = displayManager.PrimaryDisplayMonitor;
                    if (!NativeMethods.SetWindowPos(Wallpapers[0].Handle,
                        1,
                        0,
                        0,
                        screenArea.Width,
                        screenArea.Height,
                        (int)(NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE | NativeMethods.SetWindowPosFlags.SWP_NOZORDER)))
                    {
                        Logger.Info(LogUtil.GetWin32Error("Failed to update wallpaper rect."));
                    }
                }
            }
            else
            {
                int i;
                foreach (var screen in displayManager.DisplayMonitors.ToList())
                {
                    if ((i = wallpapers.FindIndex(x => x.Screen.Equals(screen))) != -1)
                    {
                        //Wallpapers[i].Play();
                        Logger.Info($"Updating wallpaper rect(Screen): {Wallpapers[i].Screen.Bounds} -> {screen.Bounds}.");
                        //For play/pause, setting the new metadata.
                        Wallpapers[i].Screen = screen;

                        var screenArea = displayManager.VirtualScreenBounds;
                        if (!NativeMethods.SetWindowPos(Wallpapers[i].Handle,
                            1,
                            (screen.Bounds.X - screenArea.Location.X),
                            (screen.Bounds.Y - screenArea.Location.Y),
                            (screen.Bounds.Width),
                            (screen.Bounds.Height),
                            (int)(NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE | NativeMethods.SetWindowPosFlags.SWP_NOZORDER)))
                        {
                            Logger.Info(LogUtil.GetWin32Error("Failed to update wallpaper rect."));
                        }
                    }
                }
            }
            RefreshDesktop();
        }

        private void RestoreDisconnectedWallpapers()
        {
            try
            {
                switch (userSettings.Settings.WallpaperArrangement)
                {
                    case WallpaperArrangement.per:
                        //Finding screens for previously removed wallpaper if screen reconnected..
                        var wallpapersToRestore = wallpapersDisconnected.FindAll(wallpaper => displayManager.DisplayMonitors.FirstOrDefault(
                            screen => wallpaper.Display.Equals(screen)) != null);
                        RestoreWallpaper(wallpapersToRestore);
                        break;
                    case WallpaperArrangement.span:
                        //UpdateWallpaperRect() should handle it normally.
                        //todo: if all screens disconnect?
                        break;
                    case WallpaperArrangement.duplicate:
                        if ((displayManager.DisplayMonitors.Count > Wallpapers.Count) && Wallpapers.Count != 0)
                        {
                            var newScreen = displayManager.DisplayMonitors.FirstOrDefault(screen => Wallpapers.FirstOrDefault(
                                wp => wp.Screen.Equals(screen)) == null);
                            if (newScreen != null)
                            {
                                //Only one call is required for multiple screens.
                                _ = SetWallpaperAsync(Wallpapers[0].Model, newScreen);
                            }
                        }
                        //todo: if all screens disconnect?
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.Error("Failed to restore disconnected wallpaper(s): " + e.ToString());
            }
        }

        private void RestoreWallpaper(List<WallpaperLayoutModel> wallpaperLayout)
        {
            foreach (var layout in wallpaperLayout)
            {
                LibraryModel libraryItem = null;
                try
                {
                    libraryItem = wallpaperLibraryFactory.CreateFromDirectory(layout.LivelyInfoPath);
                }
                catch (Exception e)
                {
                    Logger.Info($"Skipping restoration of {layout.LivelyInfoPath} | {e.Message}");
                    wallpapersDisconnected.Remove(layout);
                }

                var screen = displayManager.DisplayMonitors.FirstOrDefault(x => x.Equals(layout.Display));
                if (screen == null)
                {
                    Logger.Info($"Screen missing, skipping restoration of {layout.LivelyInfoPath} | {layout.Display.DeviceName}");
                    if (!wallpapersDisconnected.Contains(layout))
                    {
                        Logger.Info($"Wallpaper queued to disconnected screenlist {layout.LivelyInfoPath} | {layout.Display.DeviceName}");
                        wallpapersDisconnected.Add(new WallpaperLayoutModel((DisplayMonitor)layout.Display, layout.LivelyInfoPath));
                    }
                }
                else
                {
                    Logger.Info($"Restoring wallpaper {libraryItem.Title} | {libraryItem.LivelyInfoFolderPath}");
                    _ = SetWallpaperAsync(libraryItem, screen);
                    wallpapersDisconnected.Remove(layout);
                }
            }
        }

        /// <summary>
        /// Restore wallpaper from save.
        /// </summary>
        public void RestoreWallpaper()
        {
            try
            {
                var wallpaperLayout = userSettings.WallpaperLayout;
                if (userSettings.Settings.WallpaperArrangement == WallpaperArrangement.span ||
                    userSettings.Settings.WallpaperArrangement == WallpaperArrangement.duplicate)
                {
                    if (wallpaperLayout.Count != 0)
                    {
                        var libraryItem = wallpaperLibraryFactory.CreateFromDirectory(wallpaperLayout[0].LivelyInfoPath);
                        SetWallpaperAsync(libraryItem, displayManager.PrimaryDisplayMonitor);
                    }
                }
                else if (userSettings.Settings.WallpaperArrangement == WallpaperArrangement.per)
                {
                    RestoreWallpaper(wallpaperLayout);
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to restore wallpaper: {e}");
            }
        }

        public void CloseAllWallpapers()
        {
            CloseAllWallpapers(fireEvent: true);
        }

        private void CloseAllWallpapers(bool fireEvent)
        {
            if (Wallpapers.Count > 0)
            {
                wallpapers.ForEach(x =>
                {
                    x.Close();
                    x.Dispose();
                });
                wallpapers.Clear();
                watchdog.Clear();

                if (fireEvent)
                    WallpaperChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void CloseWallpaper(DisplayMonitor display)
        {
            CloseWallpaper(display: display, fireEvent: true);
        }

        private void CloseWallpaper(DisplayMonitor display, bool fireEvent)
        {
            var tmp = wallpapers.FindAll(x => x.Screen.Equals(display));
            if (tmp.Count > 0)
            {
                tmp.ForEach(x =>
                {
                    if (x.Pid is int pid)
                        watchdog.Remove(pid);

                    x.Close();
                    x.Dispose();
                });
                wallpapers.RemoveAll(x => tmp.Contains(x));

                if (fireEvent)
                    WallpaperChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void CloseWallpaper(WallpaperType type)
        {
            var tmp = wallpapers.FindAll(x => x.Category == type);
            if (tmp.Count > 0)
            {
                tmp.ForEach(x =>
                {
                    if (x.Pid is int pid)
                        watchdog.Remove(pid);

                    x.Close();
                    x.Dispose();
                });
                wallpapers.RemoveAll(x => tmp.Contains(x));
                WallpaperChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void CloseWallpaper(LibraryModel wp)
        {
            CloseWallpaper(wp: wp, fireEvent: true);
        }

        private void CloseWallpaper(LibraryModel wp, bool fireEvent)
        {
            //NOTE: To maintain compatibility with existing code ILibraryModel is still used.
            var tmp = wallpapers.FindAll(x => x.Model.LivelyInfoFolderPath == wp.LivelyInfoFolderPath);
            if (tmp.Count > 0)
            {
                tmp.ForEach(x =>
                {
                    if (x.Pid is int pid)
                        watchdog.Remove(pid);

                    x.Close();
                    x.Dispose();
                });
                wallpapers.RemoveAll(x => tmp.Contains(x));

                if (fireEvent)
                    WallpaperChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SendMessageWallpaper(string info_path, IpcMessage msg)
        {
            wallpapers.ForEach(x =>
            {
                if (x.Model.LivelyInfoFolderPath == info_path)
                    x.SendMessage(msg);
            });
        }

        public void SendMessageWallpaper(DisplayMonitor display, string info_path, IpcMessage msg)
        {
            wallpapers.ForEach(x =>
            {
                if (x.Screen.Equals(display) && info_path == x.Model.LivelyInfoFolderPath)
                    x.SendMessage(msg);
            });
        }

        /// <summary>
        /// Force redraw desktop - clears wallpaper persisting on screen.
        /// </summary>
        public void RefreshDesktop()
        {
            // Otherwise will destroy the current WorkerW
            if (isRaisedDesktopWithLayeredShellView)
                return;

            NativeMethods.SystemParametersInfo(NativeMethods.SPI_SETDESKWALLPAPER, 0, null, NativeMethods.SPIF_UPDATEINIFILE);
        }

        private bool TryAttachToDesktop(IntPtr hwnd)
        {
            if (IsWindows7)
            {
                if (!WindowUtil.TrySetParent(hwnd, progman))
                    return false;
            }
            else
            {
                if (isRaisedDesktopWithLayeredShellView && !isWorkerWParent)
                {
                    WindowUtil.SetWindowStyle(hwnd, NativeMethods.WindowStyles.WS_CHILD);
                    // Adds WS_EX_LAYERED if required.
                    // Note: Godot fails to apply WS_EX_LAYERED if attached after SetParent.
                    WindowUtil.SetWindowTransparency(hwnd, 255);

                    if (!WindowUtil.TrySetParent(hwnd, progman))
                        return false;

                    var windowFlags = (int)(NativeMethods.SetWindowPosFlags.SWP_NOMOVE
                        | NativeMethods.SetWindowPosFlags.SWP_NOSIZE
                        | NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE);

                    NativeMethods.SetWindowPos(hwnd,
                        (int)shellDLL_DefView,
                        0,
                        0,
                        0,
                        0,
                        windowFlags);
                    EnsureWorkerWZOrder();
                }
                else
                {
                    if (!WindowUtil.TrySetParent(hwnd, workerW))
                        return false;
                }
            }
            return true;
        }

        private void EnsureWorkerWZOrder()
        {
            if (!isRaisedDesktopWithLayeredShellView || isWorkerWParent)
                return;

            if (WindowUtil.GetLastChildWindow(progman) != workerW)
            {
                Logger.Error("Unexpected WorkerW Z-order.");
                var windowFlags = (int)(NativeMethods.SetWindowPosFlags.SWP_NOMOVE
                    | NativeMethods.SetWindowPosFlags.SWP_NOSIZE
                    | NativeMethods.SetWindowPosFlags.SWP_NOACTIVATE);

                NativeMethods.SetWindowPos(workerW,
                    (int)NativeMethods.HWNDInsertAfter.HWND_BOTTOM,
                    0,
                    0,
                    0,
                    0,
                    windowFlags);
            }
        }

        private async void WndProc_TaskbarCreated(object sender, EventArgs e)
        {
            Logger.Info("WM_TASKBARCREATED: New taskbar created.");
            int newExplorerPid = GetTaskbarExplorerPid();
            if (prevExplorerPid != newExplorerPid)
            {
                // Detect explorer crash because otherwise dpi change also sends WM_TASKBARCREATED.
                Logger.Info($"Explorer crashed, pid mismatch: {prevExplorerPid} != {newExplorerPid}");
                if ((DateTime.Now - prevExplorerCrashTime).TotalSeconds > userSettings.Settings.TaskbarCrashTimeOutDelay)
                {
                    await ResetWallpaperAsync();
                }
                else
                {
                    Logger.Warn("Explorer restarted multiple times in the last 30s.");
                    _ = Task.Run(() => MessageBox.Show(Properties.Resources.DescExplorerCrash,
                            $"{Properties.Resources.TitleAppName} - {Properties.Resources.TextError}",
                            MessageBoxButton.OK, MessageBoxImage.Error));
                    CloseAllWallpapers();
                    await ResetWallpaperAsync();
                }
                prevExplorerCrashTime = DateTime.Now;
                prevExplorerPid = newExplorerPid;
            }

        }

        private async void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                // Raw input delivery can silently die across a lock/unlock cycle;
                // re-register so input forwarding keeps working without an app restart.
                try
                {
                    // SessionSwitch arrives on a non-UI thread; the WPF window
                    // (and its hwnd) must be touched from the dispatcher only.
                    System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                    {
                        this.rawInput.ReRegisterRawInputDevices();
                        Logger.Info("Raw input devices re-registered after unlock.");
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }

                // The unlock-time re-registration races with the desktop stack
                // still restoring; schedule one more pass a few seconds later
                // as a safety net (fire-and-forget, does not block wallpaper restore).
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    try
                    {
                        System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                        {
                            this.rawInput.ReRegisterRawInputDevices();
                            Logger.Info("Raw input devices re-registered (unlock second pass).");
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex);
                    }
                });

                if (userSettings.Settings.IsRestartAfterLockscreen)
                {
                    Logger.Info("Desktop unlocked, resetting wallpaper service.");
                    await ResetWallpaperAsync();
                }
                else
                {
                    //Issue: https://github.com/rocksdanister/lively/issues/802
                    if (!(DesktopWorkerW == IntPtr.Zero || NativeMethods.IsWindow(DesktopWorkerW)))
                    {
                        Logger.Info("WorkerW invalid after unlock, resetting..");
                        await ResetWallpaperAsync();
                    }
                    else
                    {
                        await Task.Delay(1000);
                        if (Wallpapers.Any(x => x.IsExited))
                        {
                            Logger.Info("Wallpaper crashed after unlock, resetting..");
                            await ResetWallpaperAsync();
                        }
                    }
                }
            }
        }

        private void RawInput_MouseMoveRaw(object sender, MouseRawArgs e)
        {
            // Don't forward when not on desktop unless configured.
            if (userSettings.Settings.InputForward == InputForwardMode.off || !IsDesktop() && !userSettings.Settings.MouseInputMovAlways)
                return;

            try
            {
                ForwardMouseToWallpapers(e.X, e.Y, InputUtil.MouseMove);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private void RawInput_MouseDownRaw(object sender, MouseClickRawArgs e)
        {
            if (userSettings.Settings.InputForward == InputForwardMode.off || !IsDesktop())
                return;

            try
            {
                switch (e.Button)
                {
                    case RawInputMouseBtn.left:
                        if (!InputUtil.IsMouseButtonsSwapped)
                            ForwardMouseToWallpapers(e.X, e.Y, InputUtil.MouseLeftButtonDown);
                        break;
                    case RawInputMouseBtn.right:
                        if (InputUtil.IsMouseButtonsSwapped)
                            ForwardMouseToWallpapers(e.X, e.Y, InputUtil.MouseLeftButtonDown);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private void RawInput_MouseUpRaw(object sender, MouseClickRawArgs e)
        {
            if (userSettings.Settings.InputForward == InputForwardMode.off || !IsDesktop())
                return;

            try
            {
                switch (e.Button)
                {
                    case RawInputMouseBtn.left:
                        if (!InputUtil.IsMouseButtonsSwapped)
                            ForwardMouseToWallpapers(e.X, e.Y, InputUtil.MouseLeftButtonUp);
                        break;
                    case RawInputMouseBtn.right:
                        if (InputUtil.IsMouseButtonsSwapped)
                            ForwardMouseToWallpapers(e.X, e.Y, InputUtil.MouseLeftButtonUp);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private long lastWheelFwdLog;
        private long lastWheelWpLog;

        private static bool IsCursorOverWindow(IntPtr hwnd, int x, int y)
        {
            var hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
            if (hit == IntPtr.Zero)
                return false;
            // Both resolve to the same top-level window (the WorkerW chain) when the
            // cursor actually hovers the wallpaper; any covering app window breaks the match.
            return NativeMethods.GetAncestor(hit, NativeMethods.GetAncestorFlags.GetRoot) ==
                NativeMethods.GetAncestor(hwnd, NativeMethods.GetAncestorFlags.GetRoot);
        }

        private void RawInput_MouseWheelRaw(object sender, MouseWheelRawArgs e)
        {
            if (userSettings.Settings.InputForward == InputForwardMode.off)
                return;

            try
            {
                // Detect active wallpaper based on cursor.
                var display = displayManager.GetDisplayMonitorFromPoint(new System.Drawing.Point(e.X, e.Y));
                if (Environment.TickCount64 - lastWheelFwdLog > 1000)
                {
                    lastWheelFwdLog = Environment.TickCount64;
                    Logger.Info($"Wheel raw: ({e.X},{e.Y}) d={e.Delta} fgDesktop={IsDesktop()} disp={display?.DeviceId} nWp={Wallpapers.Count}");
                }
                foreach (var wallpaper in Wallpapers)
                {
                    var allowed = wallpaper.Category.IsDeviceInputAllowed();
                    var dispEq = display.Equals(wallpaper.Screen) || userSettings.Settings.WallpaperArrangement == WallpaperArrangement.span;
                    if (Environment.TickCount64 - lastWheelWpLog > 1000)
                    {
                        lastWheelWpLog = Environment.TickCount64;
                        Logger.Info($"Wheel wp: allowed={allowed} dispEq={dispEq} cat={wallpaper.Category} screen={wallpaper.Screen?.DeviceId} hwnd={wallpaper.InputHandle}");
                    }
                    if (!allowed || !dispEq)
                        continue;
                    // Unlike clicks, hovering does not give the desktop foreground focus,
                    // so requiring IsDesktop() alone would break hover-scrolling entirely.
                    // Forward when the desktop is focused OR the cursor truly hovers this wallpaper.
                    var overWallpaper = IsCursorOverWindow(wallpaper.InputHandle, e.X, e.Y);
                    if (Environment.TickCount64 - lastWheelFwdLog > 1000)
                    {
                        lastWheelFwdLog = Environment.TickCount64;
                        Logger.Info($"Wheel fwd check: overWp={overWallpaper}");
                    }
                    if (!IsDesktop() && !overWallpaper)
                        continue;
                    // WM_MOUSEWHEEL lParam is in screen coordinates; raw cursor pos is passed as-is.
                    // WebView2 (Chromium) drops posted wheel messages, so deliver through player IPC + CDP instead.
                    // NB: the OS only synthesizes legacy queue wheel when the foreground is an app (not the
                    // desktop); the player-side thread hook covers the rare window-aimed delivery as backup.
                    if (wallpaper is WebWebView2 webWallpaper)
                        webWallpaper.SendMessage(new LivelyWheelCmd
                        {
                            X = e.X,
                            Y = e.Y,
                            Delta = e.Horizontal ? 0 : e.Delta,
                            DeltaX = e.Horizontal ? e.Delta : 0,
                        });
                    else
                        InputUtil.MouseWheel(wallpaper.InputHandle, e.X, e.Y, e.Delta);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        // 前台线程当前持有键盘焦点的窗口（壁纸 WebView2 输入法模式时 = 壁纸浏览器子窗口）
        private static IntPtr ForegroundFocusHwnd()
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero)
                return IntPtr.Zero;
            var tid = NativeMethods.GetWindowThreadProcessId(fg, out _);
            var info = new NativeMethods.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            return NativeMethods.GetGUIThreadInfo(tid, ref info) ? info.hwndFocus : IntPtr.Zero;
        }

        private void RawInput_KeyboardClickRaw(object sender, KeyboardClickRawArgs e)
        {
            try
            {
                // Don't forward when not on desktop.
                if (userSettings.Settings.InputForward != InputForwardMode.mousekeyboard || !IsDesktop())
                    return;

                // Detect active wallpaper based on cursor.
                if (!NativeMethods.GetCursorPos(out NativeMethods.POINT P))
                    return;

                // 壁纸 WebView2 已持有真实键盘焦点（star-map 输入法模式）时：
                // 系统 WM_KEYDOWN 直接投递给它，再转发一份会造成双字符。
                var focus = ForegroundFocusHwnd();
                var display = displayManager.GetDisplayMonitorFromPoint(new System.Drawing.Point(P.X, P.Y));
                foreach (var wallpaper in Wallpapers)
                {
                    if (wallpaper.Category.IsDeviceInputAllowed() &&
                        (display.Equals(wallpaper.Screen) || userSettings.Settings.WallpaperArrangement == WallpaperArrangement.span))
                    {
                        if (focus != IntPtr.Zero &&
                            (focus == wallpaper.InputHandle || NativeMethods.IsChild(wallpaper.InputHandle, focus)))
                            continue;
                        InputUtil.ForwardMessageKeyboard(wallpaper.InputHandle, e.WindowMessage, e.VirtualKey, e.ScanCode, e.IsKeyDown);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private void ForwardMouseToWallpapers(int x, int y, Action<IntPtr, int, int> forwardAction)
        {
            var display = displayManager.GetDisplayMonitorFromPoint(new System.Drawing.Point(x, y));
            var pos = userSettings.Settings.WallpaperArrangement switch
            {
                WallpaperArrangement.per => InputUtil.ToMouseDisplayLocal(x, y, display.Bounds),
                WallpaperArrangement.span => InputUtil.ToMouseSpanLocal(x, y, displayManager.VirtualScreenBounds),
                WallpaperArrangement.duplicate => InputUtil.ToMouseDisplayLocal(x, y, display.Bounds),
                _ => InputUtil.ToMouseDisplayLocal(x, y, display.Bounds),
            };

            foreach (var wallpaper in Wallpapers)
            {
                if (wallpaper.Category.IsDeviceInputAllowed() &&
                    (wallpaper.Screen.Equals(display) || userSettings.Settings.WallpaperArrangement == WallpaperArrangement.span))
                {
                    forwardAction(wallpaper.InputHandle, pos.X, pos.Y);
                }
            }
        }

        private void Playback_WallpaperControlChanged(object sender, WallpaperControlEventArgs e)
        {
            try
            {
                foreach (var wallpaper in Wallpapers)
                {
                    // Skip if targeting specific display and this isn't it
                    if (e.Display != null && !wallpaper.Screen.Equals(e.Display))
                        continue;

                    switch (e.Action)
                    {
                        case WallpaperControlAction.Pause:
                            wallpaper.Pause();
                            break;
                        case WallpaperControlAction.Play:
                            wallpaper.Play();
                            break;
                        case WallpaperControlAction.SetVolume:
                            wallpaper.SetVolume(e.Volume ?? 0);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        public void StartScreensaverIdleTimer(uint idleTime)
        {
            if (idleTime == 0)
            {
                StopScreensaverIdleTimer();
            }
            else
            {
                Logger.Info("Starting screensaver idle wait {0}ms..", idleTime);
                screensaverIdleWaitTime = idleTime;
                screensaverIdleTimer.Start();
            }
        }

        public void StopScreensaverIdleTimer()
        {
            if (screensaverIdleTimer.Enabled)
            {
                Logger.Info("Stopping screensaver idle wait..");
                screensaverIdleTimer.Stop();
            }
        }

        private async void ScreensaverIdleTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                // Only start if input idle, non exclusive window and smtc media playback.
                if (SystemIdleUtil.GetLastInputTime() >= screensaverIdleWaitTime
                    && !SystemIdleUtil.IsExclusiveFullScreenAppRunning()
                    && !await SystemIdleUtil.IsSmtcPlayingAsync())
                {
                    // Check active audio session for application not supporting smtc (games and older media players.)
                    if (!userSettings.Settings.ScreensaverAudioIdleCheck ||
                        SystemIdleUtil.GetActiveAudioSessionPids().All(p => Wallpapers.Any(w => w.Pid != null && w.Pid == p)))
                    {
                        // All audio is from wallpapers.
                        await screensaver.StartAsync(userSettings.Settings.ScreensaverFadeIn);
                    }
                    else
                    {
                        // Audio session active, skipping screensaver.
                    }
                }
            }
            catch (Exception ex) {
                Logger.Error(ex);
            }
        }

        private bool IsDesktop()
        {
            IntPtr hWnd = NativeMethods.GetForegroundWindow();
            return (IntPtr.Equals(hWnd, original_WorkerW) || IntPtr.Equals(hWnd, progman));
        }

        private static int GetTaskbarExplorerPid()
        {
            _ = NativeMethods.GetWindowThreadProcessId(NativeMethods.FindWindow("Shell_TrayWnd", null), out int pid);
            return pid;
        }

        private static bool IsWindows7 => 
            Environment.OSVersion.Version.Major == 6 && Environment.OSVersion.Version.Minor == 1;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    WallpaperChanged -= SetupDesktop_WallpaperChanged;
                    workerWHook?.Dispose();
                    screensaverIdleTimer?.Dispose();
                    CloseAllWallpapers(false);
                    RefreshDesktop();

                    //not required.. (need to restart if used.)
                    //NativeMethods.SendMessage(workerw, (int)NativeMethods.WM.CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~WinDesktopCore()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
