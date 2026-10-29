using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace NetworkIsolator.Core;

/// <summary>
/// File drop from Explorer that also works when this window is elevated.
/// WPF's own drag and drop (OLE) is blocked by Windows between a normal Explorer and an
/// administrator window, so the classic WM_DROPFILES mechanism is used instead.
/// </summary>
public static class ShellFileDrop
{
    private const int WmDropFiles = 0x0233;
    private const uint WmCopyData = 0x004A;
    private const uint WmCopyGlobalData = 0x0049;
    private const uint MessageFilterAllow = 1;

    /// <summary>Call once the window handle exists (OnSourceInitialized). <paramref name="onFiles"/> runs on the UI thread.</summary>
    public static void Enable(Window window, Action<string[]> onFiles)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        HwndSource? source = HwndSource.FromHwnd(hwnd);
        if (source is null) return;

        foreach (uint message in new[] { (uint)WmDropFiles, WmCopyData, WmCopyGlobalData })
            NativeMethods.ChangeWindowMessageFilterEx(hwnd, message, MessageFilterAllow, IntPtr.Zero);

        NativeMethods.DragAcceptFiles(hwnd, true);

        source.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg != WmDropFiles) return IntPtr.Zero;

            string[] files = ReadFiles(wParam);
            handled = true;

            // Not inline: a dialog opened here would keep Explorer waiting for the drop to finish.
            window.Dispatcher.BeginInvoke(() => onFiles(files));
            return IntPtr.Zero;
        });
    }

    private static string[] ReadFiles(IntPtr hDrop)
    {
        try
        {
            uint count = NativeMethods.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
            var files = new string[count];
            for (uint i = 0; i < count; i++)
            {
                uint length = NativeMethods.DragQueryFileW(hDrop, i, null, 0);
                var path = new StringBuilder((int)length + 1);
                NativeMethods.DragQueryFileW(hDrop, i, path, length + 1);
                files[i] = path.ToString();
            }
            return files;
        }
        finally
        {
            NativeMethods.DragFinish(hDrop);
        }
    }
}
