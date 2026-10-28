using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Lively.Views.WindowMsg
{
    /// <summary>
    /// Precision Touchpad (PTP) raw input subscription and two-finger scroll gesture.
    ///
    /// The OS synthesizes legacy wheel messages for touchpad two-finger scroll only when
    /// the foreground is an app window; hovering the desktop routes the gesture through
    /// WM_POINTERWHEEL which explorer's icon view consumes, so the wallpaper-side raw
    /// mouse listener never sees it. Subscribing to the touchpad's PTP HID collection
    /// (usagePage 0x0D, usage 0x05, RIDEV_INPUTSINK) taps the same gesture at the source
    /// without interfering with system handling. A recognized scroll is raised as a
    /// MouseWheelRaw event exactly like a physical wheel, so the downstream forwarding
    /// gate and player IPC are reused unchanged.
    /// Parsing follows github.com/emoacht/RawInput.Touchpad.
    /// </summary>
    internal static class PtpWheel
    {
        #region Win32

        [DllImport("User32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices(
            RAWINPUTDEVICE[] pRawInputDevices,
            uint uiNumDevices,
            uint cbSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        private const uint RIDEV_REMOVE = 0x00000001;
        private const uint RIDEV_INPUTSINK = 0x00000100;

        [DllImport("User32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);

        private const uint RID_INPUT = 0x10000003;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType; // 0=mouse, 1=keyboard, 2=HID
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWHID
        {
            public uint dwSizeHid;
            public uint dwCount;
            public IntPtr bRawData; // marker only; report bytes trail this struct in the buffer
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUT
        {
            public RAWINPUTHEADER Header;
            public RAWHID Hid;
        }

        [DllImport("User32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(
            IntPtr hDevice,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize);

        private const uint RIDI_PREPARSEDDATA = 0x20000005;

        [DllImport("Hid.dll", SetLastError = true)]
        private static extern uint HidP_GetCaps(
            IntPtr PreparsedData,
            out HIDP_CAPS Capabilities);

        private const uint HIDP_STATUS_SUCCESS = 0x00110000;

        [DllImport("Hid.dll", CharSet = CharSet.Auto)]
        private static extern uint HidP_GetValueCaps(
            HIDP_REPORT_TYPE ReportType,
            [Out] HIDP_VALUE_CAPS[] ValueCaps,
            ref ushort ValueCapsLength,
            IntPtr PreparsedData);

        private enum HIDP_REPORT_TYPE
        {
            HidP_Input,
            HidP_Output,
            HidP_Feature
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;

            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;

            [MarshalAs(UnmanagedType.U1)]
            public bool IsAlias;

            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;

            [MarshalAs(UnmanagedType.U1)]
            public bool IsRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsStringRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsDesignatorRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsAbsolute;
            [MarshalAs(UnmanagedType.U1)]
            public bool HasNull;

            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved2;

            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;

            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;

            // NotRange (single usage)
            public ushort Usage => UsageMin;
        }

        [DllImport("Hid.dll", CharSet = CharSet.Auto)]
        private static extern uint HidP_GetUsageValue(
            HIDP_REPORT_TYPE ReportType,
            ushort UsagePage,
            ushort LinkCollection,
            ushort Usage,
            out uint UsageValue,
            IntPtr PreparsedData,
            IntPtr Report,
            uint ReportLength);

        #endregion

        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private static long lastParseLog;

        /// <summary>
        /// Touchpad logical-unit travel to wheel-delta conversion. Synaptics pads span a
        /// few thousand logical units vertically; 0.6 makes one comfortable two-finger
        /// sweep worth roughly 3-5 wheel notches. Tune from the "Ptp wheel" logs.
        /// </summary>
        private const double TravelToWheelFactor = 0.6;

        /// <summary>Per-frame travel below this many logical units is finger jitter.</summary>
        private const double MinTravelUnits = 2.0;

        // Gesture state; only touched from the WM_INPUT hook (UI thread).
        private static readonly Dictionary<int, (int X, int Y)> prevContacts = new();
        private static long lastGestureLog;
        private static long lastFramesLog;
        private static int framesSeen;

        public static void Register(IntPtr hwnd)
        {
            var device = new RAWINPUTDEVICE
            {
                usUsagePage = 0x000D, // Digitizers page
                usUsage = 0x0005,     // Touch Pad collection
                dwFlags = RIDEV_INPUTSINK, // deliver even while not foreground
                hwndTarget = hwnd,
            };
            var ok = RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            Logger.Info($"PTP register: ok={ok} err={(ok ? 0 : Marshal.GetLastWin32Error())}");
        }

        public static void Unregister(IntPtr hwnd)
        {
            var device = new RAWINPUTDEVICE
            {
                usUsagePage = 0x000D,
                usUsage = 0x0005,
                dwFlags = RIDEV_REMOVE,
                hwndTarget = hwnd,
            };
            try
            {
                RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "PTP unregister failed");
            }
        }

        /// <summary>
        /// Consume a WM_INPUT lParam when it carries raw HID. Returns true for any HID
        /// frame (PTP handled, other HID ignored) so the caller skips the RawInput.Sharp
        /// mouse/keyboard path; false lets that path run as before.
        /// </summary>
        public static bool TryProcessInput(IntPtr lParam, RawInputMsgWindow window)
        {
            var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            uint size = 0;
            if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0)
                return false;

            var buffer = Marshal.AllocHGlobal((int)size);
            byte[] raw;
            IntPtr device = IntPtr.Zero;
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size)
                    return false;

                var rawInput = Marshal.PtrToStructure<RAWINPUT>(buffer);
                if (rawInput.Header.dwType != 2) // RIM_TYPEHID
                    return false;
                device = rawInput.Header.hDevice;

                var hidLength = (int)(rawInput.Hid.dwSizeHid * rawInput.Hid.dwCount);
                if (hidLength <= 0)
                    return true;

                raw = new byte[hidLength];
                // Report bytes occupy the tail of the RAWINPUT buffer, past header+RAWHID.
                Marshal.Copy(buffer + (int)(size - hidLength), raw, 0, hidLength);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            framesSeen++;
            if (Environment.TickCount64 - lastFramesLog > 60000)
            {
                Logger.Info($"PTP frames last 60s: {framesSeen}");
                framesSeen = 0;
                lastFramesLog = Environment.TickCount64;
            }

            var contacts = ParseContacts(device, raw);
            ProcessContacts(contacts, window);
            return true;
        }

        /// <summary>
        /// Throttled parse diagnostics (1/s); failures here were silent in the first
        /// build and cost a debugging round-trip.
        /// </summary>
        private static void LogParse(string message)
        {
            if (Environment.TickCount64 - lastParseLog > 1000)
            {
                lastParseLog = Environment.TickCount64;
                Logger.Info(message);
            }
        }

        /// <summary>
        /// Decode finger contacts from a PTP input report using HID preparsed data.
        /// Returns null when the frame is not from a touchpad collection (0x0D/0x05).
        /// </summary>
        private static TouchpadContact[] ParseContacts(IntPtr device, byte[] report)
        {
            if (device == IntPtr.Zero)
            {
                LogParse("PTP parse: null device handle");
                return null;
            }

            IntPtr preparsed = IntPtr.Zero;
            IntPtr reportPtr = Marshal.AllocHGlobal(report.Length);
            try
            {
                Marshal.Copy(report, 0, reportPtr, report.Length);

                uint preparsedSize = 0;
                if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize) != 0)
                {
                    LogParse($"PTP parse: preparsed size query failed dev={device}");
                    return null;
                }

                preparsed = Marshal.AllocHGlobal((int)preparsedSize);
                if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, preparsed, ref preparsedSize) != preparsedSize)
                {
                    LogParse("PTP parse: preparsed fill failed");
                    return null;
                }

                if (HidP_GetCaps(preparsed, out var caps) != HIDP_STATUS_SUCCESS)
                {
                    LogParse("PTP parse: HidP_GetCaps failed");
                    return null;
                }

                // Only the touchpad top-level collection, not other digitizers (pen/touch).
                if (caps.UsagePage != 0x0D || caps.Usage != 0x0005)
                {
                    LogParse($"PTP parse: caps mismatch usagePage={caps.UsagePage:X} usage={caps.Usage:X}");
                    return null;
                }

                ushort valueCapsLength = caps.NumberInputValueCaps;
                var valueCaps = new HIDP_VALUE_CAPS[valueCapsLength];
                if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, valueCaps, ref valueCapsLength, preparsed) != HIDP_STATUS_SUCCESS)
                {
                    LogParse($"PTP parse: HidP_GetValueCaps failed n={caps.NumberInputValueCaps}");
                    return null;
                }

                uint contactCount = 0;
                var creator = new TouchpadContactCreator();
                var contacts = new List<TouchpadContact>();

                foreach (var valueCap in valueCaps.OrderBy(x => x.LinkCollection))
                {
                    if (HidP_GetUsageValue(
                        HIDP_REPORT_TYPE.HidP_Input,
                        valueCap.UsagePage,
                        valueCap.LinkCollection,
                        valueCap.Usage,
                        out var value,
                        preparsed,
                        reportPtr,
                        (uint)report.Length) != HIDP_STATUS_SUCCESS)
                    {
                        continue;
                    }

                    // Usage page/id per Windows Precision Touchpad input reports:
                    // docs.microsoft.com/windows-hardware/design/component-guidelines/
                    // windows-precision-touchpad-required-hid-top-level-collections
                    switch (valueCap.LinkCollection)
                    {
                        case 0:
                            switch (valueCap.UsagePage, valueCap.Usage)
                            {
                                case (0x0D, 0x54): // Contact Count
                                    contactCount = value;
                                    break;
                                // 0x56 Scan Time unused here.
                            }
                            break;
                        default:
                            switch (valueCap.UsagePage, valueCap.Usage)
                            {
                                case (0x0D, 0x51): // Contact ID
                                    creator.ContactId = (int)value;
                                    break;
                                case (0x01, 0x30): // X
                                    creator.X = (int)value;
                                    break;
                                case (0x01, 0x31): // Y
                                    creator.Y = (int)value;
                                    break;
                            }
                            break;
                    }

                    if (creator.TryCreate(out var contact))
                    {
                        contacts.Add(contact);
                        // contactCount==0 (field unreadable) degrades to "parse all caps"
                        // instead of truncating to a single contact.
                        if (contactCount > 0 && contacts.Count >= contactCount)
                            break;
                        creator.Clear();
                    }
                }

                LogParse($"PTP parse: valueCaps={valueCapsLength} cc={contactCount} contacts={contacts.Count} " +
                    $"ids=[{string.Join(",", contacts.Select(c => c.ContactId.ToString()))}] " +
                    $"pts=[{string.Join(" ", contacts.Select(c => $"{c.X},{c.Y}"))}]");

                return contacts.ToArray();
            }
            finally
            {
                Marshal.FreeHGlobal(reportPtr);
                if (preparsed != IntPtr.Zero)
                    Marshal.FreeHGlobal(preparsed);
            }
        }

        /// <summary>
        /// Two-finger scroll state machine. Frames with exactly two tracked contacts move
        /// the gesture; any other frame count (idle, single-finger cursor, 3+ finger
        /// system gestures) resets it. Touchdown and finger-swap frames only record, so
        /// no jump leaks into the scroll.
        /// </summary>
        private static void ProcessContacts(TouchpadContact[] contacts, RawInputMsgWindow window)
        {
            try
            {
                var n = contacts?.Length ?? -1;
                if (n != 2)
                {
                    // Not a two-finger frame (idle, single-finger cursor move, 3+ finger
                    // system gestures) — any ongoing scroll ends.
                    if (prevContacts.Count > 0)
                        prevContacts.Clear();
                    Diag($"reset n={n}");
                    return;
                }

                var wasTwoFinger = prevContacts.Count == 2;
                var matched = 0;
                double dy0 = 0, dy1 = 0;
                foreach (var c in contacts)
                {
                    if (!prevContacts.TryGetValue(c.ContactId, out var prev))
                        continue;
                    if (matched == 0)
                        dy0 = c.Y - prev.Y;
                    else if (matched == 1)
                        dy1 = c.Y - prev.Y;
                    matched++;
                }

                prevContacts.Clear();
                foreach (var c in contacts)
                    prevContacts[c.ContactId] = (c.X, c.Y);

                if (!wasTwoFinger || matched != 2)
                {
                    // Fresh two-finger touchdown or finger swap: record only, no jump.
                    Diag($"track wasTwo={wasTwoFinger} matched={matched}");
                    return;
                }

                // Opposite-direction fingers = pinch/rotate, not scroll.
                if (dy0 * dy1 < 0)
                {
                    Diag($"pinch dy0={dy0:F0} dy1={dy1:F0}");
                    return;
                }

                var avgDy = (dy0 + dy1) / 2.0;

                // 2026-10-25 sign fix: this pad's raw PTP contact Y actually grows upward,
                // so a two-finger swipe down yields negative avgDy; without negation the
                // emitted wheel matches a physical wheel (backward roll = negative delta)
                // and the single DOM-convention flip in ForwardWheelToPage governs both
                // devices the same way. The old negation double-flipped touchpad vs mouse.
                var wheel = (int)Math.Round(avgDy * TravelToWheelFactor);

                if (Math.Abs(avgDy) < MinTravelUnits || wheel == 0)
                {
                    Diag($"filtered avgDy={avgDy:F1}");
                    return;
                }

                if (!Lively.Common.Helpers.Pinvoke.NativeMethods.GetCursorPos(out var p))
                    return;

                window.RaisePtpWheel(p.X, p.Y, wheel);

                if (Environment.TickCount64 - lastGestureLog > 1000)
                {
                    lastGestureLog = Environment.TickCount64;
                    Logger.Info($"Ptp wheel: dY={avgDy:F1} wheel={wheel}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PTP gesture error");
            }
        }

        /// <summary>Throttled gesture-state diagnostics (1/s).</summary>
        private static void Diag(string state)
        {
            if (Environment.TickCount64 - lastGestureLog > 1000)
            {
                lastGestureLog = Environment.TickCount64;
                Logger.Info($"PTP gesture: {state}");
            }
        }

        public readonly struct TouchpadContact
        {
            public int ContactId { get; }
            public int X { get; }
            public int Y { get; }

            public TouchpadContact(int contactId, int x, int y) =>
                (ContactId, X, Y) = (contactId, x, y);
        }

        private class TouchpadContactCreator
        {
            public int? ContactId { get; set; }
            public int? X { get; set; }
            public int? Y { get; set; }

            public bool TryCreate(out TouchpadContact contact)
            {
                if (ContactId.HasValue && X.HasValue && Y.HasValue)
                {
                    contact = new TouchpadContact(ContactId.Value, X.Value, Y.Value);
                    return true;
                }
                contact = default;
                return false;
            }

            public void Clear()
            {
                ContactId = null;
                X = null;
                Y = null;
            }
        }
    }
}
