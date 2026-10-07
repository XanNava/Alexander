using Alexander.Shared;

namespace Alexander.Services.Automation;

// "watch" - uses a FileSystemWatcher on the definition's folder and runs
// its command whenever a matching file event fires. Overlapping runs
// aren't serialized (a burst of file events - e.g. a bulk save - can
// launch several commands at once); reasonable for a first version, worth
// revisiting if that turns out to matter in practice.
public sealed class FileWatcherService(
	IOutputWriter output,
	IAppLog log) : IFolderService {

	[Flags]
	private enum WatchedEvents {
		None = 0,
		Changed = 1,
		Created = 2,
		Deleted = 4,
		Renamed = 8
	}

	private FileSystemWatcher? watcher;
	private ServiceDefinition? definition;
	private CancellationToken lifetimeToken;

	public string TypeName => "watch";
	public string TypeDescription => "Runs a command when a matching file event occurs.";
	public ServiceStatus Status { get; private set; } = ServiceStatus.Stopped;

	public Task StartAsync(
		ServiceDefinition startedDefinition,
		CancellationToken cancellationToken) {

		definition = startedDefinition;
		lifetimeToken = cancellationToken;

		var newWatcher = new FileSystemWatcher(startedDefinition.Folder) {
			Filter = string.IsNullOrWhiteSpace(startedDefinition.WatchPattern)
				? "*.*"
				: startedDefinition.WatchPattern,
			IncludeSubdirectories = startedDefinition.IncludeSubdirectories,
			NotifyFilter = NotifyFilters.LastWrite
				| NotifyFilters.FileName
				| NotifyFilters.DirectoryName
		};

		var events = ParseEvents(startedDefinition.WatchEvents);

		if (events.HasFlag(WatchedEvents.Changed)) {
			newWatcher.Changed += OnEvent;
		}

		if (events.HasFlag(WatchedEvents.Created)) {
			newWatcher.Created += OnEvent;
		}

		if (events.HasFlag(WatchedEvents.Deleted)) {
			newWatcher.Deleted += OnEvent;
		}

		if (events.HasFlag(WatchedEvents.Renamed)) {
			newWatcher.Renamed += OnEvent;
		}

		newWatcher.Error += (_, args) => {
			log.WriteLine(
				$"[{startedDefinition.Name}] watcher error: " +
				args.GetException().Message);
		};

		watcher = newWatcher;
		newWatcher.EnableRaisingEvents = true;
		Status = ServiceStatus.Running;

		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken) {
		watcher?.Dispose();
		watcher = null;
		Status = ServiceStatus.Stopped;
		return Task.CompletedTask;
	}

	// Fire-and-forget is deliberate here: FileSystemWatcher's own event
	// callback is already running on a thread-pool thread, not blocking
	// anything the interactive prompt cares about, so there's nothing to
	// hand this back to.
	private void OnEvent(object sender, FileSystemEventArgs args) {
		if (definition is null || lifetimeToken.IsCancellationRequested) {
			return;
		}

		_ = RunAsync(args);
	}

	private async Task RunAsync(FileSystemEventArgs args) {
		var current = definition!;

		log.WriteLine(
			$"[{current.Name}] triggered by {args.ChangeType} on " +
			$"{args.Name}; running: {current.Command}");

		try {
			var result = await ShellCommandRunner.RunShellCommandAsync(
				current.Command,
				current.Folder,
				line => log.WriteLine($"[{current.Name}] {line}"),
				line => log.WriteLine($"[{current.Name}] ERROR: {line}"),
				lifetimeToken);

			Report(current, $"{args.ChangeType} on {args.Name} -> exit {result.ExitCode}.");
		}
		catch (OperationCanceledException) {
			// Stopped mid-run - fine, nothing to report.
		}
		catch (Exception exception) when (
			exception is IOException or System.ComponentModel.Win32Exception) {

			Report(current, $"failed to run after {args.ChangeType} on {args.Name}: {exception.Message}");
		}
	}

	private void Report(ServiceDefinition current, string message) {
		var line = $"[{current.Name}] {message}";
		output.WriteLine(line);
		log.WriteLine(line);
	}

	private static WatchedEvents ParseEvents(string? spec) {
		const WatchedEvents all = WatchedEvents.Changed
			| WatchedEvents.Created
			| WatchedEvents.Deleted
			| WatchedEvents.Renamed;

		if (string.IsNullOrWhiteSpace(spec)) {
			return all;
		}

		var result = WatchedEvents.None;

		foreach (var part in spec.Split(
			',',
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {

			if (Enum.TryParse<WatchedEvents>(part, ignoreCase: true, out var parsed)) {
				result |= parsed;
			}
		}

		return result == WatchedEvents.None ? all : result;
	}
}
