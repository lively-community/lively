namespace Lively.Models.Message
{
    public enum MessageType
    {
        msg_hwnd,
        msg_console,
        msg_wploaded,
        msg_screenshot,
        cmd_reload,
        cmd_close,
        cmd_screenshot,
        cmd_suspend,
        cmd_resume,
        cmd_volume,
        lsp_perfcntr,
        lsp_nowplaying,
        lp_slider,
        lp_textbox,
        lp_dropdown,
        lp_fdropdown,
        lp_button,
        lp_cpicker,
        lp_chekbox,
        lp_dropdown_scaler,
        // Append new values only: existing players (VLC etc.) deserialize by numeric value.
        cmd_wheel,
    }
}
