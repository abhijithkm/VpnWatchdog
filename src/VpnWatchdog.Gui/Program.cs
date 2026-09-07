using System.Runtime.InteropServices;

namespace VpnWatchdog.Gui;

internal static class Program
{
    // Local\, not Global\: this only ever needs to stop the SAME user from
    // running a second copy, and Local\ needs no elevated privileges either
    // way.
    private const string SingleInstanceMutexName = @"Local\VpnWatchdog-SingleInstance";

    [STAThread]
    private static void Main()
    {
        using Mutex mutex = new(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // A second monitoring loop and a second tray icon would fight the
            // first over the same VPN profile, so hand focus to the running
            // instance instead of starting one. Broadcast rather than
            // FindWindow-by-title: it still reaches the other instance's
            // hidden window even while it's minimised to tray.
            NativeMethods.PostMessage(
                NativeMethods.HwndBroadcast, MainForm.ShowExistingInstanceMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal static class NativeMethods
{
    public static readonly IntPtr HwndBroadcast = new(0xffff);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string message);
}
