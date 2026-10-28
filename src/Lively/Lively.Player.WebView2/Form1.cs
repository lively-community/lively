using CommandLine;
using Lively.Common;
using Lively.Common.Extensions;
using Lively.Common.Helpers;
using Lively.Common.Helpers.Pinvoke;
using Lively.Common.JsonConverters;
using Lively.Common.Services;
using Lively.Models.Enums;
using Lively.Models.Message;
using Lively.Player.WebView2.Extensions.WebView2;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebView = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Lively.Player.WebView2
{
    public partial class Form1 : Form
    {
        private WebView webView;
        private StartArgs startArgs;
        private bool isPaused = false;
        private bool isVideoStream = false;
        private int cefD3DRenderingSubProcessId;

        private bool initializedServices = false; //delay API init till loaded page
        private IAudioVisualizerService visualizerService;
        private IHardwareUsageService hardwareUsageService;
        private INowPlayingService nowPlayingService;

        // ------------------------------------------------------------------
        // Touchpad wheel delivery (Win11 precision touchpad): two-finger scroll
        // is synthesized by the OS as classic WM_MOUSEWHEEL with fine-grained
        // deltas and delivered straight to the window under the cursor, without
        // entering the raw input queue (the main app's raw-input listener never
        // sees it) - and WebView2 drops wheel messages outright. A thread-local
        // WH_GETMESSAGE hook observes every message retrieved on this UI thread
        // for the whole window chain (this form + WebView2's Chrome children),
        // so wheel is intercepted here and synthesized inside the page instead.
        // ------------------------------------------------------------------
        private const int WH_GETMESSAGE = 3;
        private const uint WM_NULL = 0x0000;
        private const uint WM_MOUSEWHEEL = 0x020A;
        private const uint WM_MOUSEHWHEEL = 0x020E;
        private const uint WM_POINTERWHEEL = 0x020F;
        private const uint WM_POINTERHWHEEL = 0x0210;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSGINFO
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        private IntPtr wheelHook;
        private HookProc wheelHookProc; // rooted to prevent GC while installed

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            wheelHookProc = WheelMsgHookProc;
            wheelHook = SetWindowsHookEx(WH_GETMESSAGE, wheelHookProc,
                GetModuleHandle(null), GetCurrentThreadId());
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (wheelHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(wheelHook);
                wheelHook = IntPtr.Zero;
            }
            base.OnHandleDestroyed(e);
        }

        private IntPtr WheelMsgHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // HC_ACTION (0) with nonzero wParam: the message is being removed;
            // peek-only passes (wParam == 0) are skipped to avoid double handling.
            if (nCode == 0 && wParam != IntPtr.Zero && lParam != IntPtr.Zero && !IsDisposed)
            {
                var m = (MSGINFO)Marshal.PtrToStructure(lParam, typeof(MSGINFO));
                var isWheel = m.message == WM_MOUSEWHEEL || m.message == WM_MOUSEHWHEEL
                    || m.message == WM_POINTERWHEEL || m.message == WM_POINTERHWHEEL;
                // GA_ROOT (2): only messages aimed at this form's window chain;
                // the WebView2 Chrome children resolve to this form as root.
                if (isWheel && GetAncestor(m.hwnd, 2) == Handle)
                {
                    var horizontal = m.message == WM_MOUSEHWHEEL || m.message == WM_POINTERHWHEEL;
                    var delta = (short)((long)m.wParam >> 16);
                    // Pointer-wheel deltas can carry huge flick values; clamp so a
                    // single event cannot jump the page by whole screens.
                    if (delta > 400) delta = 400;
                    if (delta < -400) delta = -400;
                    var x = (short)((long)m.lParam & 0xFFFF);
                    var y = (short)(((long)m.lParam >> 16) & 0xFFFF);
                    _ = ForwardWheelToPage(new LivelyWheelCmd
                    {
                        X = x,
                        Y = y,
                        Delta = horizontal ? 0 : delta,
                        DeltaX = horizontal ? delta : 0,
                    });
                    // WebView2 drops these anyway; rewrite to WM_NULL so the page
                    // can never receive a double delivery if Chromium changes.
                    Marshal.WriteInt32(lParam, 8, (int)WM_NULL);
                }
            }
            return CallNextHookEx(wheelHook, nCode, wParam, lParam);
        }

        private bool IsDebugging { get; } = BuildInfoUtil.IsDebugBuild();

        public Form1()
        {
            InitializeComponent();
            if (IsDebugging)
            {
                startArgs = new StartArgs
                {
                    // .html fullpath
                    Url = "https://google.com/",
                    //online or local(file)
                    Type = WebPageType.online,
                    // LivelyProperties.json path if any
                    Properties = @"",
                    SysInfo = false,
                    NowPlaying = false,
                    AudioVisualizer = false,
                    PauseEvent = false
                };

                this.FormBorderStyle = FormBorderStyle.Sizable;
                this.WindowState = FormWindowState.Normal;
                this.StartPosition = FormStartPosition.Manual;
                this.Size = new Size(1920, 1080);
                this.ShowInTaskbar = true;
                this.MaximizeBox = true;
                this.MinimizeBox = true;
            }
            else
            {
                Parser.Default.ParseArguments<StartArgs>(Environment.GetCommandLineArgs())
                    .WithParsed((x) => startArgs = x)
                    .WithNotParsed(HandleParseError);

                this.WindowState = FormWindowState.Minimized;
                this.StartPosition = FormStartPosition.Manual;
                this.Location = new Point(-9999, 0);

                if (startArgs.Geometry != null)
                {
                    var msg = startArgs.Geometry.Split('x');
                    if (msg.Length >= 2 && int.TryParse(msg[0], out int width) && int.TryParse(msg[1], out int height))
                    {
                        this.Size = new Size(width, height);
                    }
                }

                var darkColor = Color.FromArgb(30, 30, 30);
                var lightColor = Color.FromArgb(240, 240, 240);
                this.BackColor = startArgs.Theme switch
                {
                    AppTheme.Auto => ThemeUtil.GetWindowsTheme() == AppTheme.Dark ? darkColor : lightColor,
                    AppTheme.Light => lightColor,
                    AppTheme.Dark => darkColor,
                    _ => darkColor,
                };
            }

            InitializeWebView2Async().Await(() => {
                _ = ListenToParent();
            }, 
            (err) =>
            {
                err.SendError(SendToParent, "Failed to initialize WebView2");
                // Exit or display custom error page.
                Environment.Exit(1);
            });
        }

        // Hide from taskview and taskbar.
        // ShowInTaskbar = true does not create TOOLWINDOW.
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        private void HandleParseError(IEnumerable<Error> errs)
        {
            if (errs != null)
                string.Join(Environment.NewLine, errs).SendError(SendToParent, "Error parsing launch arguments");

            // ERROR_INVALID_PARAMETER
            // Ref: <https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--0-499->
            Environment.Exit(87);
        }

        public async Task InitializeWebView2Async()
        {
            webView = new WebView() {
                DefaultBackgroundColor = Color.Transparent
            };
            webView.NavigationCompleted += WebView_NavigationCompleted;

            var webViewStartArgs =
                // Allow media autoplay even if not muted.
                "--autoplay-policy=no-user-gesture-required " +
                // Disable SMTC.
                "--disable-features=HardwareMediaKeyHandling ";
            CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions(webViewStartArgs);
            // WebView2 does not have in-memory mode, ref: https://github.com/MicrosoftEdge/WebView2Feedback/issues/3637
            // Custom user data folder, ref: https://docs.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder
            $"Setting UserData path: {startArgs.UserDataPath}".SendLog(SendToParent);
            var env = await CoreWebView2Environment.CreateAsync(null, startArgs.UserDataPath, options);
            await webView.EnsureCoreWebView2Async(env);

            // Defaults
            webView.CoreWebView2.IsMuted = startArgs.Volume == 0;
            webView.CoreWebView2.Profile.PreferredColorScheme = startArgs.Theme.GetPreferredColorScheme();
            if (startArgs.Scale != null)
            {
                $"Attempt to set scale: {startArgs.Scale.Value}".SendLog(SendToParent);
                try
                {
                    // RasterizationScale is better choice than ZoomFactor since it is not affected by system text size but not exposed in .NET, ref:
                    // https://github.com/MicrosoftEdge/WebView2Feedback/issues/4775
                    // https://github.com/MicrosoftEdge/WebView2Feedback/issues/3839
                    // https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.rasterizationscale
                    var field = typeof(WebView).GetField("_coreWebView2Controller", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (field?.GetValue(webView) is CoreWebView2Controller controller)
                    {
                        controller.ShouldDetectMonitorScaleChanges = false;
                        // Bug: Initial value is always 1, ref: https://github.com/MicrosoftEdge/WebView2Feedback/issues/5329
                        $"Current scale: {controller.RasterizationScale}".SendLog(SendToParent);
                        controller.RasterizationScale = startArgs.Scale.Value;
                        $"Updated scale: {controller.RasterizationScale}".SendLog(SendToParent);
                    }
                    else
                    {
                        "CoreWebView2Controller not found".SendError(SendToParent);
                    }
                }
                catch (Exception ex)
                {
                    ex.SendError(SendToParent, "Failed to set scale");
                }
            }

            if (!IsDebugging)
            {
                // Don't allow contextmenu and devtools.
                webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            }

            if (!string.IsNullOrWhiteSpace(startArgs.DebugPort) && int.TryParse(startArgs.DebugPort, out int _))
            {
                // In WebView2 --remote-debugging-port=XXXX is not opening up a direct connection, we will just ignore port and open DevTools instead.
                webView.CoreWebView2.OpenDevToolsWindow();
                webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            }

            webView.CoreWebView2.ProcessFailed += CoreWebView2_ProcessFailed;
            webView.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            webView.CoreWebView2.DownloadStarting += CoreWebView2_DownloadStarting;
            webView.CoreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            switch (startArgs.Type)
            {
                case WebPageType.online:
                    {
                        var (contentType, id) = WebContentUtil.GetContentType(startArgs.Url);
                        switch (contentType)
                        {
                            case WebContentType.shadertoy:
                                {
                                    $"Opening shadertoy shader: {id}".SendLog(SendToParent);
                                    webView.CoreWebView2.Navigate($"https://www.shadertoy.com/embed/{id}?gui=false&t=10&paused=false&muted=true");
                                }
                                break;
                            case WebContentType.youtube:
                                {
                                    isVideoStream = true;
                                    $"Opening yt stream: {id}".SendLog(SendToParent);
                                    // Error 153, ref: https://github.com/rocksdanister/lively/issues/2991
                                    webView.CoreWebView2.AddWebResourceRequestedFilter("*youtube.com/*", CoreWebView2WebResourceContext.All);
                                    webView.CoreWebView2.WebResourceRequested += (s, e) =>
                                    {
                                        var headers = e.Request.Headers;
                                        if (!headers.Contains("Referer"))
                                            headers.SetHeader("Referer", "https://localhost/");
                                    };
                                    webView.CoreWebView2.Navigate($"https://www.youtube.com/embed/{id}?version=3&rel=0&autoplay=1&loop=1&controls=0&playlist={id}");
                                }
                                break;
                            case WebContentType.generic:
                                {
                                    $"Opening address: {startArgs.Url}".SendLog(SendToParent);
                                    webView.CoreWebView2.Navigate(startArgs.Url);
                                }
                                break;
                            case WebContentType.none:
                                {
                                    "Failed to parse url".SendError(SendToParent);
                                    throw new ArgumentException(startArgs.Url);
                                }
                            default:
                                throw new NotImplementedException();
                        }
                    }
                    break;
                case WebPageType.local:
                    {
                        $"Opening local project: {startArgs.Url}".SendLog(SendToParent);
                        webView.NavigateToLocalPath(startArgs.Url);
                    }
                    break;
            }
            this.Controls.Add(webView);
            webView.Dock = DockStyle.Fill;

            // Make sure to do this after WebView control is attached to form to avoid issues.
            SendToParent(new LivelyMessageHwnd() {
                Hwnd = webView.Handle.ToInt32()
            });
        }

        private void CoreWebView2_DocumentTitleChanged(object sender, object e)
        {
            this.Invoke((MethodInvoker)(() => this.Text = webView.CoreWebView2.DocumentTitle));
        }

        // —— 壁纸输入法支持（star-map 页触发）——
        // 页面 input 聚焦时 postMessage({"type":"star-input-focus",x,y,w,h})（设备像素，相对壁纸客户区）：
        // 播放器在输入框正上方浮一个原生无边框小窗 + TextBox。原生顶层窗口能拿真实前台，
        // 系统 IME（中文输入法）随之挂上——彻底绕开"桌面持前台、壁纸子窗口抢焦点"的抖动战争
        // （旧方案的 SetFocus 抢焦点会被系统反复抢回，焦点抖动=页面提示反复弹、IME 挂不稳）。
        // Enter=提交（ExecuteScriptAsync 回填页面并检索）；Esc/点到别处=关闭并通知页面失焦。
        // 原生框持前台期间主程序键盘转发自动停（IsDesktop()=false），无双字符。
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private NativeInputBox nativeInput;

        // 原生输入小窗：1px 亮边 + 深底文本框，视觉贴近星图查询框
        private class NativeInputBox : Form
        {
            public TextBox Box;
            public NativeInputBox()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Color.FromArgb(62, 66, 70);          // 充当 1px 边框
                Padding = new Padding(1);
                var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 17, 18), Padding = new Padding(9, 7, 9, 6) };
                Box = new TextBox
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(16, 17, 18),
                    ForeColor = Color.FromArgb(232, 231, 226),
                    BorderStyle = BorderStyle.None,
                    Font = new Font("Microsoft YaHei UI", 10.5f)
                };
                panel.Controls.Add(Box);
                Controls.Add(panel);
            }
        }

        private void ShowNativeInput(int x, int y, int w, int h)
        {
            if (nativeInput == null)
            {
                nativeInput = new NativeInputBox();
                nativeInput.Box.KeyDown += (s, ev) =>
                {
                    if (ev.KeyCode == Keys.Enter)
                    {
                        ev.SuppressKeyPress = true;
                        var text = nativeInput.Box.Text;
                        HideNativeInput(false);
                        RunPageScript($"window.__starNativeSubmit && window.__starNativeSubmit({JsonConvert.SerializeObject(text)})");
                    }
                    else if (ev.KeyCode == Keys.Escape)
                    {
                        ev.SuppressKeyPress = true;
                        HideNativeInput(true);
                    }
                };
                nativeInput.Deactivate += (s, ev) => nativeInput.BeginInvoke((MethodInvoker)(() => HideNativeInput(true)));
            }
            nativeInput.Bounds = new Rectangle(this.PointToScreen(new Point(x, y)), new Size(Math.Max(w, 220), Math.Max(h, 26) + 16));
            nativeInput.Box.Text = string.Empty;
            nativeInput.Show(this);
            // 后台进程直接 SetForegroundWindow 会被系统拒绝：挂上前台线程的输入队列再顶前台（经典手法）
            var fg = NativeMethods.GetForegroundWindow();
            var fgTid = GetWindowThreadProcessId(fg, out _);
            var myTid = GetCurrentThreadId();
            if (fgTid != myTid) AttachThreadInput(fgTid, myTid, true);
            NativeMethods.SetForegroundWindow(nativeInput.Handle);
            if (fgTid != myTid) AttachThreadInput(fgTid, myTid, false);
            nativeInput.Activate();
            nativeInput.Box.Focus();
        }

        private void HideNativeInput(bool notifyBlur)
        {
            if (nativeInput == null || !nativeInput.Visible)
                return;
            nativeInput.Visible = false;   // 先置不可见再 Hide：Hide 触发的 Deactivate 会二次进来，靠此旗标短路
            nativeInput.Hide();
            if (notifyBlur)
                RunPageScript("window.__starNativeBlur && window.__starNativeBlur()");
        }

        private async void RunPageScript(string script)
        {
            try
            {
                if (webView?.CoreWebView2 != null)
                    await webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch { }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = (e.TryGetWebMessageAsString() ?? string.Empty).Trim();
                if (msg.StartsWith("star-input-focus"))
                {
                    int x = 40, y = 40, w = 460, h = 34;   // 无坐标时的兜底位
                    try
                    {
                        if (msg.StartsWith("{"))
                        {
                            var j = JObject.Parse(msg);
                            x = (int)j["x"]; y = (int)j["y"]; w = (int)j["w"]; h = (int)j["h"];
                        }
                    }
                    catch { }
                    "star-input-focus: native ime box".SendLog(SendToParent);
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        try { ShowNativeInput(x, y, w, h); }
                        catch (Exception ex) { ex.SendError(SendToParent, "native input failed"); }
                    }));
                }
                else if (msg == "star-input-blur")
                {
                    BeginInvoke((MethodInvoker)(() => HideNativeInput(false)));
                }
            }
            catch { }
        }

        private void CoreWebView2_ProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            // Expected behavior: DebugActiveProcess(CEF_D3DRenderingSubProcess)
            // Ref: https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2processfailedkind
            if (isPaused && e.Reason == CoreWebView2ProcessFailedReason.Unresponsive)
                return;

            e.Reason.ToString().SendError(SendToParent, "CoreWebView2 process failed");
        }

        private void CoreWebView2_NewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            // Avoid popups.
            if (e.IsUserInitiated)
            {
                e.Handled = true;
                // Open new tab/window  hyperlink (target="_blank") in default browser.
                LinkUtil.OpenBrowser(e.Uri);
            }
        }

        private void CoreWebView2_DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            // Cancel user requested downloads.
            e.Cancel = true;
        }

        private async void WebView_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            // Restore default.
            webView.DefaultBackgroundColor = Color.White;

            if (!e.IsSuccess)
            {
                e.WebErrorStatus.ToString().SendError(SendToParent, "WebView navigation failed");
                return;
            }

            if (!webView.TryGetCefD3DRenderingSubProcessId(out cefD3DRenderingSubProcessId))
                "Failed to retrieve GetCefD3DRenderingSubProcessId".SendError(SendToParent);

            switch (startArgs.Type)
            {
                case WebPageType.online:
                    switch (WebContentUtil.GetContentType(startArgs.Url).ContentType)
                    {
                        case WebContentType.shadertoy:
                            await webView.TryHideShaderToyGui();
                            break;
                    }
                    break;
                case WebPageType.local:
                    await RestoreLivelyProperties(startArgs.Properties);
                    break;
            }
            SendToParent(new LivelyMessageWallpaperLoaded() { Success = e.IsSuccess });

            if (!initializedServices)
            {
                initializedServices = true;
                if (startArgs.NowPlaying)
                {
                    nowPlayingService = new NpsmNowPlayingService();
                    nowPlayingService.NowPlayingTrackChanged += (s, e) => {
                        try
                        {
                            if (isPaused)
                                return;

                            // CefSharp CanExecuteJavascriptInMainFrame equivalent in webview required?
                            this.Invoke((Action)(() =>
                            {
                                _ = webView.ExecuteScriptFunctionAsync("livelyCurrentTrack", JsonConvert.SerializeObject(e, Formatting.Indented));
                            }));

                        }
                        catch (Exception ex)
                        {
                            ex.SendError(SendToParent, "Error sending track information");

                        }
                    };
                    nowPlayingService.Start();
                }


                if (startArgs.SysInfo)
                {
                    hardwareUsageService = new HardwareUsageService();
                    hardwareUsageService.HWMonitor += (s, e) => {
                        try
                        {
                            if (isPaused)
                                return;

                            // CefSharp CanExecuteJavascriptInMainFrame equivalent in webview required?
                            this.Invoke((Action)(() =>
                            {
                                _ = webView.ExecuteScriptFunctionAsync("livelySystemInformation", JsonConvert.SerializeObject(e, Formatting.Indented));
                            }));
                        }
                        catch { }
                    };
                    hardwareUsageService.Start();
                }

                if (startArgs.AudioVisualizer)
                {
                    visualizerService = new NAudioVisualizerService();
                    visualizerService.AudioDataAvailable += (s, e) => {
                        try
                        {
                            if (isPaused)
                                return;

                            //TODO: CefSharp CanExecuteJavascriptInMainFrame equivalent in webview
                            this.Invoke((Action)(() =>
                            {
                                _ = webView.ExecuteScriptFunctionAsync("livelyAudioListener", e);
                            }));
                        }
                        catch { }
                    };
                    visualizerService.Start(startArgs.AudioVisualizerDeviceId);
                }
            }
        }

        /// <summary>
        /// Resumes the suspended CEF Direct3D rendering subprocess by detaching the debugger.
        /// Must be called on the same thread that previously attached, since debugger state is thread-specific.
        /// </summary>
        private void ResumeRenderingSubProcess()
        {
            if (cefD3DRenderingSubProcessId == 0)
                return;

            _ = NativeMethods.DebugActiveProcessStop((uint)cefD3DRenderingSubProcessId);
        }

        /// <summary>
        /// Suspends the CEF Direct3D rendering subprocess by attaching a debugger.
        /// Must be called on the same thread to ensure proper detachment later.
        /// </summary>
        private void SuspendRenderingSubProcess()
        {
            // The "System Idle Process" is given process ID 0, Kernel is 1.
            if (cefD3DRenderingSubProcessId == 0)
                return;

            // DebugSetProcessKillOnExit by default is TRUE.
            // Ref: https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-debugsetprocesskillonexit
            _ = NativeMethods.DebugActiveProcess((uint)cefD3DRenderingSubProcessId);
        }

        public async Task ListenToParent()
        {
            if (IsDebugging)
                return;

            var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

            try
            {
                await Task.Run(async () =>
                {
                    while (true)
                    {
                        // Since UTF8 is backward compatible, will work without this reader for non unicode characters.
                        string text = await reader.ReadLineAsync();
                        if (startArgs.VerboseLog)
                            Console.WriteLine(text);

                        if (string.IsNullOrEmpty(text))
                        {
                            // When the redirected stream is closed, a null line is sent to the event handler. 
                            break;
                        }
                        else
                        {
                            try
                            {
                                var obj = JsonConvert.DeserializeObject<IpcMessage>(text, new JsonSerializerSettings() { Converters = { new IpcMessageConverter() } });
                                if (obj.Type == MessageType.cmd_close)
                                    break;

                                // Ensure all processing is on the same thread.
                                // Some WebView2 calls require UI thread.
                                this.Invoke((Action)(async () =>
                                {
                                    try
                                    {
                                        await ProcessMessage(obj);
                                    }
                                    catch (Exception ie1)
                                    {
                                        ie1.SendError(SendToParent);
                                    }
                                }));
                            }
                            catch (Exception ie2)
                            {
                                ie2.SendError(SendToParent);
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                ex.SendError(SendToParent);
            }
            finally
            {
                reader?.Dispose();
                this.Invoke((Action)Application.Exit);
            }
        }

        private async Task ProcessMessage(IpcMessage obj)
        {
            switch (obj.Type)
            {
                case MessageType.cmd_reload:
                    // ConnectionAborted issue.
                    //try
                    //{
                    //    webView?.Reload();
                    //}
                    //catch (Exception ie)
                    //{
                    //    WriteToParent(new LivelyMessageConsole()
                    //    {
                    //        Category = ConsoleMessageType.error,
                    //        Message = $"Reload failed: {ie.Message}"
                    //    });
                    //}
                    break;
                case MessageType.cmd_suspend:
                    await HandleSuspend();
                    break;
                case MessageType.cmd_resume:
                    await HandleResume();
                    break;
                case MessageType.cmd_volume:
                    var vc = (LivelyVolumeCmd)obj;
                    webView.CoreWebView2.IsMuted = vc.Volume == 0;
                    break;
                case MessageType.cmd_wheel:
                    await ForwardWheelToPage((LivelyWheelCmd)obj);
                    break;
                case MessageType.cmd_screenshot:
                    var success = true;
                    var scr = (LivelyScreenshotCmd)obj;
                    try
                    {
                        await webView.CaptureScreenshot(scr.Format, scr.FilePath);
                    }
                    catch (Exception ie)
                    {
                        success = false;
                        ie.SendError(SendToParent, "Failed to capture screenshot");
                    }
                    finally
                    {
                        SendToParent(new LivelyMessageScreenshot()
                        {
                            FileName = Path.GetFileName(scr.FilePath),
                            Success = success
                        });
                    }
                    break;
                case MessageType.lp_slider:
                    var sl = (LivelySlider)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", sl.Name, sl.Value);
                    break;
                case MessageType.lp_textbox:
                    var tb = (LivelyTextBox)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", tb.Name, tb.Value);
                    break;
                case MessageType.lp_dropdown:
                    var dd = (LivelyDropdown)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", dd.Name, dd.Value);
                    break;
                case MessageType.lp_cpicker:
                    var cp = (LivelyColorPicker)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", cp.Name, cp.Value);
                    break;
                case MessageType.lp_chekbox:
                    var cb = (LivelyCheckbox)obj;
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", cb.Name, cb.Value);
                    break;
                case MessageType.lp_fdropdown:
                    var fd = (LivelyFolderDropdown)obj;
                    var filePath = fd.Value is null ? null : Path.Combine(Path.GetDirectoryName(startArgs.Url), fd.Value);
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", fd.Name, File.Exists(filePath) ? fd.Value : null);
                    break;
                case MessageType.lp_button:
                    var btn = (LivelyButton)obj;
                    if (btn.IsDefault)
                        await RestoreLivelyProperties(startArgs.Properties);
                    else
                        await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", btn.Name, true);
                    break;
                case MessageType.lsp_perfcntr:
                    await webView.ExecuteScriptFunctionAsync("livelySystemInformation", JsonConvert.SerializeObject(((LivelySystemInformation)obj).Info, Formatting.Indented));

                    break;
                case MessageType.lsp_nowplaying:
                    await webView.ExecuteScriptFunctionAsync("livelyCurrentTrack", JsonConvert.SerializeObject(((LivelySystemNowPlaying)obj).Info, Formatting.Indented));
                    break;
                case MessageType.cmd_close:
                    // Handle on caller
                    break;
            }
        }

        /// <summary>
        /// WebView2 (Chromium) silently drops WM_MOUSEWHEEL posted to its windows, and
        /// CDP Input.dispatchMouseEvent never completes in WebView2; the only reliable
        /// path is synthesizing the wheel event inside the page. The script also emulates
        /// the browser default action (scrolling the nearest scrollable ancestor) for
        /// events the page itself does not consume.
        /// Sign: the raw delta arrives in WM_MOUSEWHEEL convention (positive = roll
        /// forward = scroll up) while DOM deltaY/scrollTop/scrollBy use positive =
        /// scroll down; vertical is negated here so wheel/touchpad behave like every
        /// normal window (2026-10-24: missing flip inverted the whole wallpaper).
        /// Horizontal already matches (WM_MOUSEHWHEEL positive = right = DOM deltaX).
        /// </summary>
        private async Task ForwardWheelToPage(LivelyWheelCmd cmd)
        {
            if (isPaused || webView?.CoreWebView2 == null)
                return;

            // Physical screen point -> CSS viewport point.
            var origin = webView.PointToScreen(Point.Empty);
            var scale = 96f / webView.DeviceDpi;
            var x = (int)((cmd.X - origin.X) * scale);
            var y = (int)((cmd.Y - origin.Y) * scale);
            var script =
                "(function(x,y,d,dx){var el=document.elementFromPoint(x,y)||document.body;" +
                "var ev=new WheelEvent('wheel',{bubbles:true,cancelable:true,clientX:x,clientY:y,deltaX:dx||0,deltaY:d||0});" +
                "if(!el.dispatchEvent(ev))return;" +
                "var n=el;while(n&&n!==document.body){var st=getComputedStyle(n);" +
                "if(/(auto|scroll)/.test(st.overflowY)&&n.scrollHeight>n.clientHeight){n.scrollTop+=d;return;}" +
                "if(/(auto|scroll)/.test(st.overflowX)&&n.scrollWidth>n.clientWidth){n.scrollLeft+=dx;return;}" +
                "n=n.parentElement;}window.scrollBy(dx||0,d||0);})(" + x + "," + y + "," + (-cmd.Delta) + "," + cmd.DeltaX + ")";
            await webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private async Task HandleSuspend()
        {
            SuspendRenderingSubProcess();
            if (!isPaused)
            {
                if (startArgs.PauseWebMedia || isVideoStream)
                    await webView.TryPauseMedia();

                if (startArgs.PauseEvent)
                    await webView.ExecuteScriptFunctionAsync("livelyWallpaperPlaybackChanged",
                        JsonConvert.SerializeObject(new WallpaperPlaybackState() { IsPaused = true }),
                        Formatting.Indented);
            }
            isPaused = true;
        }

        private async Task HandleResume()
        {
            ResumeRenderingSubProcess();
            if (isPaused)
            {
                if (startArgs.PauseWebMedia || isVideoStream)
                    await webView.TryPlayMedia();

                if (startArgs.PauseEvent)
                    await webView.ExecuteScriptFunctionAsync("livelyWallpaperPlaybackChanged",
                            JsonConvert.SerializeObject(new WallpaperPlaybackState() { IsPaused = false }),
                            Formatting.Indented);

                if (startArgs.NowPlaying)
                    await webView.ExecuteScriptFunctionAsync("livelyCurrentTrack", JsonConvert.SerializeObject(nowPlayingService?.CurrentTrack, Formatting.Indented));
            }
            isPaused = false;
        }

        private async Task RestoreLivelyProperties(string propertyPath)
        {
            try
            {
                await LivelyPropertyUtil.LoadProperty(propertyPath, Path.GetDirectoryName(startArgs.Url), async (key, value) =>
                {
                    await webView.ExecuteScriptFunctionAsync("livelyPropertyListener", key, value);
                });
            }
            catch (Exception ex)
            {
                ex.SendError(SendToParent);
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            visualizerService?.Dispose();
            hardwareUsageService?.Stop();
            nowPlayingService?.Stop();
            webView?.Dispose();
        }

        private void SendToParent(IpcMessage obj)
        {
            if (!IsDebugging)
                Console.WriteLine(JsonConvert.SerializeObject(obj));

            Debug.WriteLine(JsonConvert.SerializeObject(obj));
        }

        private sealed class WallpaperPlaybackState
        {
            public bool IsPaused { get; set; }
        }
    }
}
