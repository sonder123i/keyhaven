using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AiGateway.App;

// 剪贴板写入走 Win32，不走 WPF 的 Clipboard.SetText：
// 后者是 OLE 的「SetDataObject + flush」，flush 撞上占用会抛异常但数据其实已放上（假失败），
// 撞上卡死的剪贴板宿主还会阻塞 UI 线程（点复制就卡）。
// OpenClipboard 遇到占用立刻返回 FALSE（不阻塞），拿不到就退避重试几次；
// SetClipboardData 成功后内存归系统所有，数据持久（关掉应用也还在）。
internal static class ClipboardText
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const int Attempts = 3;
    private const int BackoffMs = 20;

    public static bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var data = Encoding.Unicode.GetBytes(text + "\0");
        var global = GlobalAlloc(GmemMoveable, (UIntPtr)data.Length);
        if (global == IntPtr.Zero)
        {
            return false;
        }

        var target = GlobalLock(global);
        if (target == IntPtr.Zero)
        {
            GlobalFree(global);
            return false;
        }

        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(global);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    if (SetClipboardData(CfUnicodeText, global) != IntPtr.Zero)
                    {
                        return true;
                    }
                }
                finally
                {
                    CloseClipboard();
                }

                break;
            }

            Thread.Sleep(BackoffMs);
        }

        // 没交出去就自己回收；成功后绝不能 Free（系统已接管）
        GlobalFree(global);
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);
}