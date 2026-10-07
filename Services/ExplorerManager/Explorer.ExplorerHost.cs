using System.Collections.Concurrent;
using System.Runtime.Versioning;

using Alexander.Shared;

namespace Alexander.Services.Explorer;

// Replaces the standalone app's "serve" process. Shell COM objects only
// work on the STA thread that created them, and Alexander's commands run
// on whatever thread the shell loop uses - so this owns one dedicated STA
// thread, creates the ExplorerManager there, and runs every Open/List on
// it. It also owns the optional focus tracker (the old PollLoop).
//
// Registered as a DI singleton: when Core unloads (/rebuild or exit),
// disposing the ServiceProvider disposes this, which stops both threads -
// nothing from this assembly is left running to block the unload.
[SupportedOSPlatform("windows")]
internal sealed class ExplorerHost : IDisposable {
	private readonly BlockingCollection<Action> work = new();
	private readonly Thread worker;
	private readonly object trackerGate = new();

	// Only touched on the worker thread.
	private ExplorerManager? manager;

	private Thread? tracker;
	private ManualResetEventSlim? trackerStop;

	public ExplorerHost(IAppLog log) {
		ExplorerStore.LogSink = log.WriteLine;

		worker = new Thread(() => {
			foreach (var item in work.GetConsumingEnumerable()) {
				item();
			}
		}) {
			IsBackground = true,
			Name = "explorer-sta"
		};

		worker.SetApartmentState(ApartmentState.STA);
		worker.Start();
	}

	public bool IsTracking {
		get {
			lock (trackerGate) {
				return tracker is not null;
			}
		}
	}

	// Runs action on the STA thread, creating the manager on first use.
	// Cancelling stops waiting; an Explorer action already under way
	// still finishes.
	public Task<T> RunAsync<T>(
		Func<ExplorerManager, T> action,
		CancellationToken cancellationToken) {

		var completion = new TaskCompletionSource<T>(
			TaskCreationOptions.RunContinuationsAsynchronously);

		work.Add(() => {
			try {
				manager ??= new ExplorerManager(new ExplorerShell());
				completion.SetResult(action(manager));
			}
			catch (Exception exception) {
				completion.SetException(exception);
			}
		});

		return completion.Task.WaitAsync(cancellationToken);
	}

	public void StartTracking() {
		lock (trackerGate) {
			if (tracker is not null) {
				return;
			}

			var stop = new ManualResetEventSlim();
			var thread = new Thread(() => PollLoop(stop)) {
				IsBackground = true,
				Name = "explorer-focus"
			};

			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();

			trackerStop = stop;
			tracker = thread;
		}

		ExplorerStore.Log("focus tracking started");
	}

	public void StopTracking() {
		Thread? thread;
		ManualResetEventSlim? stop;

		lock (trackerGate) {
			thread = tracker;
			stop = trackerStop;
			tracker = null;
			trackerStop = null;
		}

		if (thread is null) {
			return;
		}

		stop?.Set();
		thread.Join(TimeSpan.FromSeconds(2));
		ExplorerStore.Log("focus tracking stopped");
	}

	public void Dispose() {
		StopTracking();
		work.CompleteAdding();
		worker.Join(TimeSpan.FromSeconds(3));
		ExplorerStore.LogSink = null;
	}

	// Watch which Explorer window the user focuses so "last used window"
	// stays accurate between opens. Runs on its own STA thread with its
	// own ExplorerShell, since COM objects can't cross threads; Remember
	// itself is thread-safe.
	private void PollLoop(ManualResetEventSlim stop) {
		ExplorerManager trackedManager;
		ExplorerShell explorer;

		try {
			trackedManager = RunAsync(m => m, CancellationToken.None).GetAwaiter().GetResult();
			explorer = new ExplorerShell();
		}
		catch (Exception exception) {
			ExplorerStore.Log($"focus tracking could not start: {exception.Message}");
			return;
		}

		(IntPtr, IntPtr) lastSignature = default;
		var lastScan = DateTime.MinValue;

		while (!stop.Wait(500)) {
			try {
				var hwnd = ExplorerNative.GetForegroundWindow();

				if (!ExplorerShell.IsExplorer(hwnd)) {
					continue;
				}

				var signature = (hwnd, ExplorerShell.ActiveTabHwnd(hwnd));

				if (signature == lastSignature
					&& DateTime.UtcNow - lastScan < TimeSpan.FromSeconds(5)) {

					continue;
				}

				lastSignature = signature;
				lastScan = DateTime.UtcNow;

				var tab = explorer.Scan().FirstOrDefault(window => window.Hwnd == hwnd)?.ActiveTab;

				if (tab != null && tab.Key.Length > 0) {
					trackedManager.Remember(hwnd, ExplorerStore.LoadConfig().Resolve(tab.Key).Group);
				}
			}
			catch (Exception exception) {
				ExplorerStore.Log($"poll error: {exception.Message}");
			}
		}
	}
}
