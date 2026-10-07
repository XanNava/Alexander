namespace Alexander.Services.Automation;

public enum ServiceStatus {
	Stopped,
	Starting,
	Running,
	Stopping,
	Faulted
}

// One saved automation: a name, which folder it operates on, what kind
// it is, and what to run. Persisted as JSON inside that folder (see
// Alexander.Platform.Shared.AppPaths.ServicesFolder) so "services can be created
// in a folder, and run from a folder" is literal - the definition travels
// with the folder itself (e.g. checked into source control alongside a
// project) rather than being tracked in some separate central registry.
public sealed class ServiceDefinition {
	public string Name { get; set; } = "";

	// Matches an IFolderService.TypeName, e.g. "repeat" or "watch".
	public string Type { get; set; } = "";

	public string Folder { get; set; } = "";
	public string Command { get; set; } = "";

	// "repeat" only: how often to run Command.
	public int? IntervalSeconds { get; set; }

	// "watch" only: which files to react to (a glob like "*.cs"; null/empty
	// means everything) and which change kinds (comma-separated
	// Changed/Created/Deleted/Renamed; null/empty means all of them).
	public string? WatchPattern { get; set; }
	public string? WatchEvents { get; set; }
	public bool IncludeSubdirectories { get; set; }
}

// One kind of folder worker - a repeater, a watcher, or a future custom
// trigger (the "internal logic" a service can use to decide when to
// activate itself is entirely up to what it does inside StartAsync; nothing
// about the registry or this interface is specific to timers or file
// events). The registry constructs a fresh instance via DI (registered
// Transient - see ServiceCollectionExtensions) each time a definition of
// this type is started, so the same type can run under several different
// names/folders at once.
public interface IFolderService {
	// Matches ServiceDefinition.Type.
	string TypeName { get; }
	string TypeDescription { get; }

	ServiceStatus Status { get; }

	// Called once, with the saved definition, when this instance starts.
	// For ongoing background work (a timer, a filesystem watcher), this
	// should kick it off (e.g. via Task.Run against cancellationToken)
	// and return once initialization is complete - it must not block for
	// the service's whole lifetime, or nothing else could run alongside it.
	Task StartAsync(
		ServiceDefinition definition,
		CancellationToken cancellationToken);

	Task StopAsync(CancellationToken cancellationToken);
}
