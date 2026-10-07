namespace Alexander.Services.Automation;

public sealed record RunningServiceInfo(
	string Name,
	string TypeName,
	string Folder,
	ServiceStatus Status);

public interface IServiceRegistry {
	// Type names that CAN be started (e.g. "repeat", "watch").
	IReadOnlyList<string> AvailableTypeNames { get; }
	string DescribeType(string typeName);

	IReadOnlyList<RunningServiceInfo> RunningInstances { get; }
	bool IsRunning(string name);

	Task StartAsync(ServiceDefinition definition, CancellationToken cancellationToken);
	Task StopAsync(string name, CancellationToken cancellationToken);

	// Called once at shutdown to stop whatever's still running, in
	// whatever order - there's no dependency graph between instances today.
	Task StopAllAsync(CancellationToken cancellationToken);
}

// Owns every running instance's identity and the CancellationTokenSource
// tied to its lifetime, so a service with real background work (a timer,
// a filesystem watcher) has a token it can run against that reliably gets
// cancelled on stop - StartAsync itself only has to kick that work off,
// not block for it, which is what actually makes instances run side by
// side rather than one after another.
//
// factories is a type name -> "give me a fresh instance" map, not a list
// of already-constructed services: each service type is registered
// Transient in DI (see ServiceCollectionExtensions.AddAlexanderServices),
// so calling a factory twice genuinely produces two independent objects -
// what lets the same TYPE (e.g. "watch") run under two different names at
// once.
public sealed class ServiceRegistry(
	IReadOnlyDictionary<string, Func<IFolderService>> factories) : IServiceRegistry {

	private readonly object gate = new();
	private readonly Dictionary<string, IFolderService> running =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, CancellationTokenSource> lifetimes =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> folderByName =
		new(StringComparer.OrdinalIgnoreCase);

	public IReadOnlyList<string> AvailableTypeNames { get; } = factories.Keys
		.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
		.ToArray();

	public string DescribeType(string typeName) {
		return Factory(typeName)().TypeDescription;
	}

	public IReadOnlyList<RunningServiceInfo> RunningInstances {
		get {
			lock (gate) {
				return running
					.Select(pair => new RunningServiceInfo(
						pair.Key,
						pair.Value.TypeName,
						folderByName.GetValueOrDefault(pair.Key, ""),
						pair.Value.Status))
					.ToArray();
			}
		}
	}

	public bool IsRunning(string name) {
		lock (gate) {
			return running.ContainsKey(name);
		}
	}

	public async Task StartAsync(
		ServiceDefinition definition,
		CancellationToken cancellationToken) {

		var factory = Factory(definition.Type);

		lock (gate) {
			if (running.ContainsKey(definition.Name)) {
				throw new InvalidOperationException(
					$"'{definition.Name}' is already running.");
			}
		}

		var instance = factory();
		var lifetime = new CancellationTokenSource();

		lock (gate) {
			running[definition.Name] = instance;
			lifetimes[definition.Name] = lifetime;
			folderByName[definition.Name] = definition.Folder;
		}

		try {
			await instance.StartAsync(definition, lifetime.Token);
		}
		catch {
			lock (gate) {
				running.Remove(definition.Name);
				lifetimes.Remove(definition.Name);
				folderByName.Remove(definition.Name);
			}

			lifetime.Dispose();
			throw;
		}
	}

	public async Task StopAsync(string name, CancellationToken cancellationToken) {
		IFolderService? instance;
		CancellationTokenSource? lifetime;

		lock (gate) {
			if (!running.Remove(name, out instance)) {
				throw new InvalidOperationException($"'{name}' is not running.");
			}

			lifetimes.Remove(name, out lifetime);
			folderByName.Remove(name);
		}

		// Signal any background work the instance kicked off in
		// StartAsync before asking it to wind down, so a run loop sees
		// cancellation and has a chance to exit cleanly before
		// StopAsync's own token (a separate "how long are we willing to
		// wait" budget) expires.
		lifetime?.Cancel();

		try {
			await instance.StopAsync(cancellationToken);
		}
		finally {
			lifetime?.Dispose();
		}
	}

	public async Task StopAllAsync(CancellationToken cancellationToken) {
		List<string> names;

		lock (gate) {
			names = running.Keys.ToList();
		}

		foreach (var name in names) {
			try {
				await StopAsync(name, cancellationToken);
			}
			catch {
				// Best-effort on shutdown: one instance failing to stop
				// cleanly shouldn't stop the others from getting a chance.
			}
		}
	}

	private Func<IFolderService> Factory(string typeName) {
		if (!factories.TryGetValue(typeName, out var factory)) {
			throw new InvalidOperationException(
				$"No service type named '{typeName}'. Available: " +
				$"{string.Join(", ", AvailableTypeNames)}.");
		}

		return factory;
	}
}
