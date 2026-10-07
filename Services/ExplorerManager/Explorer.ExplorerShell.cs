using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Alexander.Services.Explorer;

internal sealed class Tab {
	public required string Path { get; init; }
	public required string Key { get; init; }
	public IntPtr TabHwnd { get; init; }

	// The ShellWindows item (IWebBrowser2).
	public required object Com { get; init; }
}

internal sealed class ExplorerWindow {
	public IntPtr Hwnd { get; init; }
	public List<Tab> Tabs { get; } = new();
	public IntPtr ActiveTabHwnd { get; init; }

	public Tab? ActiveTab =>
		Tabs.FirstOrDefault(tab => tab.TabHwnd != IntPtr.Zero && tab.TabHwnd == ActiveTabHwnd)
		?? (Tabs.Count == 1 ? Tabs[0] : null);
}

// Wraps Shell.Application. COM objects are apartment-bound, so an
// instance must be created and used on one STA thread - ExplorerHost
// owns that thread.
[SupportedOSPlatform("windows")]
internal sealed class ExplorerShell {
	public const string ExplorerClass = "CabinetWClass";      // top-level Explorer window
	public const string TabClass = "ShellTabWindowClass";     // one per tab
	private const int CmdNewTab = 0xA21B;                     // undocumented "New tab" command id

	private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
	private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

	private readonly dynamic shell =
		Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")
			?? throw new InvalidOperationException("Shell.Application is not available."))!;

	// --- Win32 side ------------------------------------------------------

	public static List<IntPtr> ExplorerHwnds() {
		var found = new List<IntPtr>();

		ExplorerNative.EnumWindows((hwnd, _) => {
			if (ExplorerNative.ClassOf(hwnd) == ExplorerClass) {
				found.Add(hwnd);
			}

			return true;
		}, IntPtr.Zero);

		return found;
	}

	public static List<IntPtr> TabHwnds(IntPtr hwnd) {
		var found = new List<IntPtr>();

		ExplorerNative.EnumChildWindows(hwnd, (child, _) => {
			if (ExplorerNative.ClassOf(child) == TabClass) {
				found.Add(child);
			}

			return true;
		}, IntPtr.Zero);

		return found;
	}

	// The visible ShellTabWindowClass child is the tab currently shown.
	public static IntPtr ActiveTabHwnd(IntPtr hwnd) {
		return TabHwnds(hwnd).FirstOrDefault(ExplorerNative.IsWindowVisible);
	}

	public static bool IsExplorer(IntPtr hwnd) {
		return hwnd != IntPtr.Zero
			&& ExplorerNative.IsWindow(hwnd)
			&& ExplorerNative.ClassOf(hwnd) == ExplorerClass;
	}

	public static void FocusWindow(IntPtr hwnd) {
		if (ExplorerNative.IsIconic(hwnd)) {
			ExplorerNative.ShowWindow(hwnd, ExplorerNative.SW_RESTORE);
		}

		if (ExplorerNative.SetForegroundWindow(hwnd)) {
			return;
		}

		// Foreground lock workaround: a synthetic Alt press lets us take focus.
		ExplorerNative.Key(ExplorerNative.VK_MENU);

		try {
			if (!ExplorerNative.SetForegroundWindow(hwnd)) {
				ExplorerStore.Log($"could not focus window {hwnd}");
			}
		}
		finally {
			ExplorerNative.Key(ExplorerNative.VK_MENU, up: true);
		}
	}

	private static void SendCtrlTab() {
		ExplorerNative.Key(ExplorerNative.VK_CONTROL);
		ExplorerNative.Key(ExplorerNative.VK_TAB);
		ExplorerNative.Key(ExplorerNative.VK_TAB, up: true);
		ExplorerNative.Key(ExplorerNative.VK_CONTROL, up: true);
	}

	// --- COM side --------------------------------------------------------

	public List<object> Items() {
		var list = new List<object>();
		dynamic windows;
		int count;

		try {
			windows = shell.Windows();
			count = windows.Count;
		}
		catch {
			return list;
		}

		for (var index = 0; index < count; index++) {
			try {
				dynamic item = windows.Item(index);

				if (item == null) {
					continue;
				}

				string fullName = item.FullName;

				if (fullName.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)) {
					list.Add(item);
				}
			}
			catch {
				// Window closing while we enumerate.
			}
		}

		return list;
	}

	public static IntPtr HwndOf(dynamic item) {
		try {
			return new IntPtr(Convert.ToInt64(item.HWND));
		}
		catch {
			return IntPtr.Zero;
		}
	}

	public static string PathOf(dynamic item) {
		try {
			// Real path; also handles special folders.
			string path = item.Document.Folder.Self.Path;

			if (!string.IsNullOrEmpty(path)) {
				return path;
			}
		}
		catch {
		}

		try {
			string url = item.LocationURL;

			return url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
				? new Uri(url).LocalPath
				: url;
		}
		catch {
			return "";
		}
	}

	// Map a ShellWindows item to its ShellTabWindowClass hwnd via IShellBrowser.
	public static IntPtr TabHwndOf(object item) {
		try {
			var serviceProvider = (IComServiceProvider)item;
			var sid = SID_STopLevelBrowser;
			var iid = IID_IShellBrowser;

			if (serviceProvider.QueryService(ref sid, ref iid, out object browser) != 0) {
				return IntPtr.Zero;
			}

			try {
				return ((IShellBrowser)browser).GetWindow(out var hwnd) == 0 ? hwnd : IntPtr.Zero;
			}
			finally {
				Marshal.ReleaseComObject(browser);
			}
		}
		catch {
			return IntPtr.Zero;
		}
	}

	// Live snapshot of every Explorer window and the folder shown in each tab.
	public List<ExplorerWindow> Scan() {
		var byHwnd = new Dictionary<IntPtr, ExplorerWindow>();

		foreach (var item in Items()) {
			var hwnd = HwndOf(item);

			if (hwnd == IntPtr.Zero) {
				continue;
			}

			if (!byHwnd.TryGetValue(hwnd, out var window)) {
				byHwnd[hwnd] = window = new ExplorerWindow {
					Hwnd = hwnd,
					ActiveTabHwnd = ActiveTabHwnd(hwnd)
				};
			}

			string path = PathOf(item);

			window.Tabs.Add(new Tab {
				Path = path,
				Key = PathKey.Norm(path),
				TabHwnd = TabHwndOf(item),
				Com = item
			});
		}

		return byHwnd.Values.ToList();
	}

	// --- actions ---------------------------------------------------------

	// Bring a background tab to the front (the window must already be focused).
	public bool SelectTab(ExplorerWindow window, Tab tab) {
		if (tab.TabHwnd == IntPtr.Zero || window.Tabs.Count < 2) {
			return true;
		}

		for (var index = 0; index < window.Tabs.Count; index++) {
			if (ActiveTabHwnd(window.Hwnd) == tab.TabHwnd) {
				return true;
			}

			SendCtrlTab();
			Thread.Sleep(120);
		}

		return ActiveTabHwnd(window.Hwnd) == tab.TabHwnd;
	}

	public static void Navigate(Tab tab, string path) {
		((dynamic)tab.Com).Navigate2(path);
	}

	public bool OpenTab(ExplorerWindow window, string path) {
		FocusWindow(window.Hwnd);

		var parent = ActiveTabHwnd(window.Hwnd);

		if (parent == IntPtr.Zero) {
			return false;
		}

		var beforeTabs = TabHwnds(window.Hwnd).ToHashSet();
		var beforeItems = Items().Count(item => HwndOf(item) == window.Hwnd);

		ExplorerNative.PostMessage(parent, ExplorerNative.WM_COMMAND, (IntPtr)CmdNewTab, IntPtr.Zero);

		var deadline = DateTime.UtcNow.AddSeconds(3);

		while (DateTime.UtcNow < deadline) {
			Thread.Sleep(100);

			var newTab = TabHwnds(window.Hwnd).FirstOrDefault(hwnd => !beforeTabs.Contains(hwnd));

			if (newTab == IntPtr.Zero) {
				continue;
			}

			var same = Items().Where(item => HwndOf(item) == window.Hwnd).ToList();
			var target = same.FirstOrDefault(item => TabHwndOf(item) == newTab);

			// New tabs are appended.
			if (target == null && same.Count > beforeItems) {
				target = same[^1];
			}

			if (target != null) {
				((dynamic)target).Navigate2(path);
				return true;
			}
		}

		ExplorerStore.Log($"new tab did not appear in window {window.Hwnd}");
		return false;
	}

	public IntPtr OpenWindow(string path) {
		var before = ExplorerHwnds().ToHashSet();
		var key = PathKey.Norm(path);

		var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
		startInfo.ArgumentList.Add(path);
		Process.Start(startInfo);

		var start = DateTime.UtcNow;

		while (DateTime.UtcNow - start < TimeSpan.FromSeconds(6)) {
			Thread.Sleep(150);

			var fresh = ExplorerHwnds().FirstOrDefault(hwnd => !before.Contains(hwnd));

			if (fresh != IntPtr.Zero) {
				return fresh;
			}

			// Win11's "open external folder links in new tab" option may
			// have made it a tab.
			if (DateTime.UtcNow - start > TimeSpan.FromSeconds(1.5)) {
				var hit = Scan().FirstOrDefault(window => window.Tabs.Any(tab => tab.Key == key));

				if (hit != null) {
					return hit.Hwnd;
				}
			}
		}

		return IntPtr.Zero;
	}
}
