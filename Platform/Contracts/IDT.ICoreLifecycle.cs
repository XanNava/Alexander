namespace Alexander.Contracts;

// Lifecycle of one Core generation: starting it up and tearing it down.
public interface ICoreLifecycle : IAsyncDisposable {
	// Called once right after Core is (re)loaded. Returns a line to
	// print, or null. Persisted state (the current folder, saved service
	// definitions) is re-read from disk here regardless of whether this
	// is the very first launch or a reload after /rebuild - there's no
	// separate "resume" parameter, because nothing needs one: services
	// that were running before a /rebuild are stopped by the swap (see
	// Shell/CoreHost.SwapAsync) since they live in the same reloadable
	// assembly context as Core itself, but their DEFINITIONS are saved to
	// disk, so /service list shows what to restart with /service start.
	Task<string?> StartAsync(CancellationToken cancellationToken);
}
