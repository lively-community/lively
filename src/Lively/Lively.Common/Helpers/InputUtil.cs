using Lively.Common.Helpers.Pinvoke;
using System;
using System.Drawing;

namespace Lively.Common.Helpers
{
    public static class InputUtil
    {
        static readonly IntPtr MK_LBUTTON = (IntPtr)0x0001;
        static readonly IntPtr MK_RBUTTON = (IntPtr)0x0002;
        static readonly IntPtr MK_MOVE = (IntPtr)0x0020;

        public static bool IsMouseButtonsSwapped { get; } =
            NativeMethods.GetSystemMetrics((int)NativeMethods.SystemMetric.SM_SWAPBUTTON) != 0;

        public static void MouseLeftButtonDown(IntPtr hwnd, int x, int y) =>
            ForwardMessageMouse(hwnd, x, y, (int)NativeMethods.WM.LBUTTONDOWN, MK_LBUTTON);

        public static void MouseLeftButtonUp(IntPtr hwnd, int x, int y) =>
            ForwardMessageMouse(hwnd, x, y, (int)NativeMethods.WM.LBUTTONUP, MK_LBUTTON);

        public static void MouseRightButtonDown(IntPtr hwnd, int x, int y) =>
            ForwardMessageMouse(hwnd, x, y, (int)NativeMethods.WM.RBUTTONDOWN, MK_RBUTTON);

        public static void MouseRightButtonUp(IntPtr hwnd, int x, int y) =>
            ForwardMessageMouse(hwnd, x, y, (int)NativeMethods.WM.RBUTTONUP, MK_RBUTTON);

        public static void MouseMove(IntPtr hwnd, int x, int y) =>
            ForwardMessageMouse(hwnd, x, y, (int)NativeMethods.WM.MOUSEMOVE, MK_MOVE);

        public static void MouseWheel(IntPtr hwnd, int x, int y, int delta) =>
            ForwardMessageMouseWheel(hwnd, x, y, delta);

        /// <summary>
        /// Forward mouse input to the active/inactive window.
        /// </summary>
        /// <param name="hwnd">Target window handle</param>
        /// <param name="x">Cursor pos x</param>
        /// <param name="y">Cursor pos y</param>
        /// <param name="msg">mouse message</param>
        /// <param name="wParam">additional msg parameter</param>
        public static void ForwardMessageMouse(IntPtr hwnd, int x, int y, int msg, IntPtr wParam)
        {
            // The low-order word specifies the x-coordinate of the cursor, the high-order word specifies the y-coordinate of the cursor.
            // Ref: https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-mousemove
            uint lParam = Convert.ToUInt32(y);
            lParam <<= 16;
            lParam |= Convert.ToUInt32(x);
            NativeMethods.PostMessageW(hwnd, msg, wParam, (UIntPtr)lParam);
        }

        /// <summary>
        /// Forward mouse wheel scroll to the active/inactive window.
        /// </summary>
        /// <param name="hwnd">Target window handle</param>
        /// <param name="x">Cursor pos x (screen coordinates)</param>
        /// <param name="y">Cursor pos y (screen coordinates)</param>
        /// <param name="delta">Wheel delta, multiples of WHEEL_DELTA (120); positive = forward/away</param>
        public static void ForwardMessageMouseWheel(IntPtr hwnd, int x, int y, int delta)
        {
            // The high-order word of wParam specifies the wheel delta, the low-order word the key flags;
            // lParam specifies the position of the cursor in screen coordinates.
            // Ref: https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-mousewheel
            IntPtr wParam = (IntPtr)(((delta & 0xFFFF) << 16) | 0);
            // Pack as raw bits: screen coordinates can be negative on multi-monitor setups
            // (displays left of/above the primary), which Convert.ToUInt32() would reject.
            var lParam = (UIntPtr)(uint)((x & 0xFFFF) | (y << 16));
            NativeMethods.PostMessageW(hwnd, (int)NativeMethods.WM.MOUSEWHEEL, wParam, lParam);
        }

        /// <summary>
        /// Forward key input to the active/inactive window.
        /// </summary>
        /// <param name="hwnd">Target window handle</param>
        /// <param name="msg">key press msg</param>
        /// <param name="wParam">Virtual-Key code</param>
        /// <param name="scanCode">OEM code of the key</param>
        /// <param name="isPressed">Key is pressed</param>
        public static void ForwardMessageKeyboard(IntPtr hwnd, int msg, IntPtr wParam, int scanCode, bool isPressed)
        {
            //Ref:
            // https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-keydown
            // https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-keyup
            uint lParam = 1u; //press
            lParam |= (uint)scanCode << 16; //oem code
            lParam |= 1u << 24; //extended key
            lParam |= 0u << 29; //context code; Note: Alt key combos wont't work
            /* Same as:
             * lParam = isPressed ? (lParam |= 0u << 30) : (lParam |= 1u << 30); //prev key state
             * lParam = isPressed ? (lParam |= 0u << 31) : (lParam |= 1u << 31); //transition state
             */
            lParam = isPressed ? lParam : (lParam |= 3u << 30);
            NativeMethods.PostMessageW(hwnd, msg, wParam, (UIntPtr)lParam);
        }

        /// <summary>
        /// Converts global mouse position to per-display localized coordinates.
        /// </summary>
        public static Point ToMouseDisplayLocal(int x, int y, Rectangle displayBounds)
        {
            x += -1 * displayBounds.X;
            y += -1 * displayBounds.Y;
            return new Point(x, y);
        }

        /// <summary>
        /// Converts global mouse position to span-local coordinates.
        /// </summary>
        public static Point ToMouseSpanLocal(int x, int y, Rectangle virtualScreenBounds)
        {
            x -= virtualScreenBounds.Location.X;
            y -= virtualScreenBounds.Location.Y;
            return new Point(x, y);
        }
    }
}
