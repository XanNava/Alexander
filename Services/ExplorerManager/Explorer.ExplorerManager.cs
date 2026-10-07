using System.Runtime.Versioning;

namespace Alexander.Services.Explorer;

internal sealed record OpenResult(string Action, string Path, string? Group, long Hwnd);

internal sealed record WindowInfo(long Hwnd, string Title, IReadOnlyList<TabInfo> Tabs);

internal sealed record TabInfo(string Path, bool Active, string? Group);

// The decision-making half of the standalone app: given a folder and the
// rules, focus an existing tab, open a new tab, navigate a tab, or open a
// new window. Open and List touch Shell COM, so they must run on the STA
// thread that created the ExplorerShell (see ExplorerHost). Remember only
// touches user32 and the state file, so the focus tracker may call it
// from its own thread.
[SupportedOSPlatform("windows")]
internal sealed class ExplorerManager {
	private const string DefaultKey = "__default__";

	private readonly object gate = new();
	private readonly ExplorerShell explorer;
	private ExplorerState state;

	public ExplorerManager(ExplorerShell explorer) {
		this.explorer = explorer;
		state = ExplorerStore.Load<ExplorerState>(ExplorerStore.StateFile);
	}

	// --- tracking --------------------------------------------------------

	// Record that a window was just used for a group (thread-safe).
	public void Remember(IntPtr hwnd, string? group) {
		lock (gate) {
			var handle = hwnd.ToInt64();

			var mru = new List<long> { handle };
			mru.AddRange(state.Mru.Where(other => other != handle && Alive(other)).Take(49));

			var last = state.LastWindow
				.Where(pair => Alive(pair.Value))
				.ToDictionary(pair => pair.Key, pair => pair.Value);

			last[group ?? DefaultKey] = handle;

			var changed = !mru.SequenceEqual(state.Mru)
				|| last.Count != state.LastWindow.Count
				|| last.Any(pair =>
					!state.LastWindow.TryGetValue(pair.Key, out var value)
					|| value != pair.Value);

			if (!changed) {
				return;
			}

			state = new ExplorerState { Mru = mru, LastWindow = last };
			ExplorerStore.Save(ExplorerStore.StateFile, state);
		}
	}

	private static bool Alive(long hwnd) {
		return ExplorerShell.IsExplorer(new IntPtr(hwnd));
	}

	private List<ExplorerWindow> Ranked(IEnumerable<ExplorerWindow> windows) {
		var order = new Dictionary<long, int>();

		for (var index = 0; index < state.Mru.Count; index++) {
			order.TryAdd(state.Mru[index], index);
		}

		return windows
			.OrderBy(window => order.TryGetValue(window.Hwnd.ToInt64(), out var index) ? index : int.MaxValue)
			.ToList();
	}

	// --- decisions -------------------------------------------------------

	private ExplorerWindow? PickTarget(
		List<ExplorerWindow> windows,
		Policy policy,
		ExplorerConfig config) {

		IEnumerable<ExplorerWindow> candidates =
			policy.Root != null
				? windows.Where(window => window.Tabs.Any(tab => PathKey.IsUnder(tab.Key, policy.Root)))
			: policy.AvoidGroupWindows
				? windows.Where(window => !window.Tabs.Any(tab => config.InAnyGroup(tab.Key)))
			: windows;

		var list = candidates.ToList();

		if (list.Count == 0) {
			return null;
		}

		if (state.LastWindow.TryGetValue(policy.Group ?? DefaultKey, out var preferred)) {
			var hit = list.FirstOrDefault(window => window.Hwnd.ToInt64() == preferred);

			if (hit != null) {
				return hit;
			}
		}

		return Ranked(list)[0];
	}

	private (ExplorerWindow Window, Tab Tab)? FindOpen(List<ExplorerWindow> windows, string key) {
		foreach (var window in Ranked(windows)) {
			foreach (var tab in window.Tabs) {
				if (tab.Key == key) {
					return (window, tab);
				}
			}
		}

		return null;
	}

	private static Tab? GroupTab(ExplorerWindow window, Policy policy) {
		var active = window.ActiveTab;

		if (policy.Root == null) {
			return active;
		}

		if (active != null && PathKey.IsUnder(active.Key, policy.Root)) {
			return active;
		}

		return window.Tabs.FirstOrDefault(tab => PathKey.IsUnder(tab.Key, policy.Root));
	}

	// path must be an absolute path to an existing folder - the command
	// layer resolves and checks it against Alexander's current folder.
	public OpenResult Open(string path, OpenMode? modeOverride) {
		lock (gate) {
			var key = PathKey.Norm(path);

			// Reread every time: edits apply instantly.
			var config = ExplorerStore.LoadConfig();
			var policy = config.Resolve(key);

			if (modeOverride is { } mode) {
				policy = policy with { Mode = mode };
			}

			// Live: catches folders the user navigated to by hand.
			var windows = explorer.Scan();

			OpenResult Done(string action, IntPtr hwnd) {
				if (hwnd != IntPtr.Zero) {
					Remember(hwnd, policy.Group);
				}

				return new OpenResult(action, path, policy.Group, hwnd.ToInt64());
			}

			if (policy.FocusIfOpen && FindOpen(windows, key) is { } hit) {
				ExplorerShell.FocusWindow(hit.Window.Hwnd);
				explorer.SelectTab(hit.Window, hit.Tab);
				return Done("focused_existing", hit.Window.Hwnd);
			}

			var target = policy.Mode == OpenMode.NewWindow
				? null
				: PickTarget(windows, policy, config);

			if (target != null && policy.Mode == OpenMode.Tab && explorer.OpenTab(target, path)) {
				return Done("new_tab", target.Hwnd);
			}

			if (target != null && policy.Mode == OpenMode.Navigate && GroupTab(target, policy) is { } tab) {
				ExplorerShell.FocusWindow(target.Hwnd);
				explorer.SelectTab(target, tab);
				ExplorerShell.Navigate(tab, path);
				return Done("navigated", target.Hwnd);
			}

			return Done("new_window", explorer.OpenWindow(path));
		}
	}

	public List<WindowInfo> List() {
		lock (gate) {
			var config = ExplorerStore.LoadConfig();

			return Ranked(explorer.Scan())
				.Select(window => new WindowInfo(
					window.Hwnd.ToInt64(),
					ExplorerNative.TextOf(window.Hwnd),
					window.Tabs
						.Select(tab => new TabInfo(
							tab.Path,
							tab.TabHwnd != IntPtr.Zero && tab.TabHwnd == window.ActiveTabHwnd,
							tab.Key.Length > 0 ? config.Resolve(tab.Key).Group : null))
						.ToList()))
				.ToList();
		}
	}
}
