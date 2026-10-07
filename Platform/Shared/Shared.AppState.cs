namespace Alexander.Shared;

public sealed class AppState {
	public string? CurrentFolder { get; set; }
	public string? DefaultAiClientId { get; set; }
}

public interface IStateStore {
	Task<AppState> LoadAsync(CancellationToken cancellationToken);
	Task SaveAsync(AppState state, CancellationToken cancellationToken);
}

public sealed class StateStore(AppPaths paths) : IStateStore {
	public async Task<AppState> LoadAsync(
		CancellationToken cancellationToken) {

		return await JsonFiles.ReadAsync<AppState>(
			paths.StateFile,
			cancellationToken) ?? new AppState();
	}

	public Task SaveAsync(
		AppState state,
		CancellationToken cancellationToken) {

		return JsonFiles.WriteAsync(
			paths.StateFile,
			state,
			cancellationToken);
	}
}
