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
            //
            // Guarded on != 0: RegisterWindowMessage returns 0 on the (very
            // rare) failure case, and 0 is WM_NULL - a real message other
            // software sends for its own reasons. Broadcasting THAT to every
            // top-level window in the session on a registration failure would
            // be actively harmful, not just a no-op.
            if (MainForm.ShowExistingInstanceMessage != 0)
            {
                NativeMethods.PostMessage(
                    NativeMethods.HwndBroadcast, MainForm.ShowExistingInstanceMessage, IntPtr.Zero, IntPtr.Zero);
            }

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
