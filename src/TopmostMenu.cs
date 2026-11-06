using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace AIAnnounce;

// ToolStrip drop-downs are separate native windows. Keep the entire open menu stack
// above other topmost apps, without repeatedly activating a window or stealing input.
sealed class TopmostMenu : ContextMenuStrip
{
    const uint NoOwnerZOrder = 0x0200;
    static readonly HashSet<TopmostMenu> OpenMenus = new();
    readonly Timer keepAbove = new() { Interval = 150 };
    readonly HashSet<ToolStripDropDown> descendants = new();

    public TopmostMenu() => keepAbove.Tick += (_, _) => RaiseMenuStack();

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        OpenMenus.Add(this);
        WatchChildren(this);
        RaiseMenuStack();
        // WinForms can adjust owner/z-order after the Opened event returns.
        BeginInvoke(new Action(RaiseMenuStack));
        keepAbove.Start();
    }

    void WatchChildren(ToolStripDropDown parent)
    {
        foreach (var item in parent.Items.OfType<ToolStripDropDownItem>())
        {
            if (!item.HasDropDownItems) continue;
            var child = item.DropDown;
            if (descendants.Add(child)) child.Opened += ChildOpened;
            WatchChildren(child);
        }
    }

    void ChildOpened(object? sender, EventArgs e)
    {
        if (sender is ToolStripDropDown child) WatchChildren(child);
        RaiseMenuStack();
    }

    // Raise the root first, then its visible children so a submenu stays above its parent.
    internal void RaiseMenuStack()
    {
        if (IsDisposed || !Visible) return;
        Raise(this);
        void Children(ToolStripDropDown parent)
        {
            foreach (var item in parent.Items.OfType<ToolStripDropDownItem>())
            {
                if (!item.HasDropDownItems || !item.DropDown.Visible) continue;
                Raise(item.DropDown);
                Children(item.DropDown);
            }
        }
        Children(this);
    }

    static void Raise(ToolStripDropDown window)
    {
        if (window.IsHandleCreated && !window.IsDisposed)
            Native.SetWindowPos(window.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | NoOwnerZOrder);
    }

    // Called after the overlay's own periodic raise, so it cannot cover its menus.
    public static void RaiseOpenMenus()
    {
        foreach (var menu in OpenMenus.ToArray()) menu.RaiseMenuStack();
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        keepAbove.Stop();
        OpenMenus.Remove(this);
        UnwatchChildren();
        base.OnClosed(e);
    }

    void UnwatchChildren()
    {
        foreach (var child in descendants) child.Opened -= ChildOpened;
        descendants.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            keepAbove.Dispose();
            OpenMenus.Remove(this);
            UnwatchChildren();
        }
        base.Dispose(disposing);
    }
}
