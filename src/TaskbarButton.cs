using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AIAnnounce;

// A permanently minimized stand-in window that gives the app a taskbar button you can pin. The
// overlay stays a tool window with no button, which is what keeps it on every virtual desktop.
// Clicking the button toggles the overlay. The button carries the plan usage as its progress bar
// (green, yellow past 70%, red past 90%), a hand badge while a session needs you, and the overlay
// itself as its hover thumbnail.
sealed class TaskbarButton : Form
{
    const int WM_SYSCOMMAND = 0x0112, SC_RESTORE = 0xF120, SC_MAXIMIZE = 0xF030;
    const int WM_DWMSENDICONICTHUMBNAIL = 0x0323;
    const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7, DWMWA_HAS_ICONIC_BITMAP = 10, DWMWA_DISALLOW_PEEK = 11;
    const int SW_SHOWMINNOACTIVE = 7;
    const int TBPF_NOPROGRESS = 0, TBPF_NORMAL = 2, TBPF_ERROR = 4, TBPF_PAUSED = 8;

    static readonly int ButtonCreated = Native.RegisterWindowMessage("TaskbarButtonCreated");

    readonly Action clicked;
    readonly Func<Size, Bitmap?> thumbnail;
    ITaskbarList3? taskbar;
    UsageLimit? progress;
    Icon? badge;
    string? badgeText;
    int shownValue = -1, shownState = -1;
    Icon? shownBadge;

    public TaskbarButton(Icon icon, Action clicked, Func<Size, Bitmap?> thumbnail)
    {
        this.clicked = clicked;
        this.thumbnail = thumbnail;
        Text = "AI Announce";
        Icon = icon;
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        // the restore position decides which monitor "owns" the button; keep it with the pins
        var primary = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(primary.Left + 100, primary.Top + 100);
        Size = new Size(320, 200);
        Opacity = 0;  // if anything ever restores it, there's nothing to see
    }

    // Form.Show() would activate the window on the way to minimized and steal focus.
    public void Reveal() => Native.ShowWindow(Handle, SW_SHOWMINNOACTIVE);

    public void SetStatus(string title, UsageLimit? usage, Icon? badgeIcon, string? badgeDescription)
    {
        if (Text != title) Text = title;  // the button's tooltip
        progress = usage;
        badge = badgeIcon;
        badgeText = badgeDescription;
        Push();
        Native.DwmInvalidateIconicBitmaps(Handle);  // DWM asks again only while a thumbnail is up
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        Native.DwmSetWindowAttribute(Handle, DWMWA_FORCE_ICONIC_REPRESENTATION, ref on, sizeof(int));
        Native.DwmSetWindowAttribute(Handle, DWMWA_HAS_ICONIC_BITMAP, ref on, sizeof(int));
        Native.DwmSetWindowAttribute(Handle, DWMWA_DISALLOW_PEEK, ref on, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ButtonCreated)
        {
            // also arrives again after Explorer restarts; the old list object is dead by then
            try
            {
                taskbar = (ITaskbarList3)new TaskbarList();
                taskbar.HrInit();
                shownValue = shownState = -1;
                shownBadge = null;
                Push();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                Log.Write("taskbar: " + ex.Message);
                taskbar = null;
            }
        }
        else if (m.Msg == WM_SYSCOMMAND)
        {
            int command = (int)(m.WParam.ToInt64() & 0xFFF0);
            if (command is SC_RESTORE or SC_MAXIMIZE)
            {
                clicked();
                return;
            }
        }
        else if (m.Msg == WM_DWMSENDICONICTHUMBNAIL)
        {
            long lp = m.LParam.ToInt64();
            SendThumbnail(new Size((int)((lp >> 16) & 0xFFFF), (int)(lp & 0xFFFF)));
            return;
        }
        base.WndProc(ref m);
    }

    // Restored by some other route (Alt+Tab, Task View): treat it as a click and drop back down.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated || Native.IsIconic(Handle)) return;
        Native.ShowWindow(Handle, SW_SHOWMINNOACTIVE);
        clicked();
    }

    void Push()
    {
        if (taskbar == null) return;
        try
        {
            int state = progress == null ? TBPF_NOPROGRESS
                : progress.Critical ? TBPF_ERROR
                : progress.Warning ? TBPF_PAUSED
                : TBPF_NORMAL;
            int value = progress == null ? 0 : (int)Math.Round(Math.Clamp(progress.Percent, 0, 100));
            if (state != shownState)
            {
                taskbar.SetProgressState(Handle, state);
                shownState = state;
                shownValue = -1;
            }
            if (state != TBPF_NOPROGRESS && value != shownValue)
            {
                // a 0% bar is invisible; keep a sliver so the button reads as "metered"
                taskbar.SetProgressValue(Handle, (ulong)Math.Max(value, 2), 100);
                shownValue = value;
            }
            if (!ReferenceEquals(badge, shownBadge))
            {
                taskbar.SetOverlayIcon(Handle, badge?.Handle ?? IntPtr.Zero, badgeText);
                shownBadge = badge;
            }
        }
        catch (COMException ex)
        {
            Log.Write("taskbar: " + ex.Message);
        }
    }

    void SendThumbnail(Size max)
    {
        using var image = thumbnail(max);
        if (image == null) return;
        IntPtr bitmap = image.GetHbitmap(Color.FromArgb(0));
        try { Native.DwmSetIconicThumbnail(Handle, bitmap, 0); }
        finally { Native.DeleteObject(bitmap); }
    }
}

[ComImport, Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
class TaskbarList { }

[ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ITaskbarList3
{
    // ITaskbarList
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);
    // ITaskbarList2
    void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    // ITaskbarList3
    void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
    void SetProgressState(IntPtr hwnd, int flags);
    void RegisterTab(IntPtr tab, IntPtr mdi);
    void UnregisterTab(IntPtr tab);
    void SetTabOrder(IntPtr tab, IntPtr insertBefore);
    void SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
    void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
    void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
    void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
    void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
    void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
}
