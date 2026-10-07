using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Alexander.Services.Explorer;

// user32 imports and the two COM interfaces ExplorerShell needs. The
// console-attach and AllowSetForegroundWindow imports from the standalone
// app are gone: there's no separate client process to hand focus over.
[SupportedOSPlatform("windows")]
internal static class ExplorerNative {
	public const uint WM_COMMAND = 0x0111;
	public const int SW_RESTORE = 9;
	public const byte VK_MENU = 0x12, VK_CONTROL = 0x11, VK_TAB = 0x09;
	public const uint KEYEVENTF_KEYUP = 0x0002;

	public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

	[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
	[DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
	[DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
	[DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
	[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
	[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
	[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
	[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
	[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
	[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
	[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

	public static string ClassOf(IntPtr hwnd) {
		var sb = new StringBuilder(256);
		return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
	}

	public static string TextOf(IntPtr hwnd) {
		var sb = new StringBuilder(512);
		return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
	}

	public static void Key(byte vk, bool up = false) {
		keybd_event(vk, 0, up ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
	}
}

// COM IServiceProvider (named to avoid clashing with System.IServiceProvider).
[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComServiceProvider {
	[PreserveSig]
	int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
}

// Only the first vtable slot (IOleWindow.GetWindow) is needed: it returns
// the tab's hwnd.
[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser {
	[PreserveSig]
	int GetWindow(out IntPtr phwnd);
}
