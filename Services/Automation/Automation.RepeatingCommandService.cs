using Alexander.Shared;

namespace Alexander.Services.Automation;

// "repeat" - the simplest of the built-in trigger kinds: no external
// signal needed, just runs its command on a fixed interval until stopped.
public sealed class RepeatingCommandService(
	IOutputWriter output,
	IAppLog log) : IFolderService {

	private Task? loop;

	public string TypeName => "repeat";
	public string TypeDescription => "Runs a command on a fixed interval.";
	public ServiceStatus Status { get; private set; } = ServiceStatus.Stopped;

	public Task StartAsync(
		ServiceDefinition definition,
		CancellationToken cancellationToken) {

		var interval = TimeSpan.FromSeconds(Math.Max(1, definition.IntervalSeconds ?? 60));

		Status = ServiceStatus.Running;

		// Kicked off, not awaited - StartAsync returning is what lets
		// other services (and the interactive prompt) run alongside this
		// one instead of everything waiting on its first interval.
		loop = Task.Run(
			() => RunLoopAsync(definition, interval, cancellationToken),
			cancellationToken);

		return Task.CompletedTask;
	}

	public async Task StopAsync(CancellationToken cancellationToken) {
		Status = ServiceStatus.Stopping;

		if (loop is not null) {
			try {
				await loop.WaitAsync(cancellationToken);
			}
			catch (OperationCanceledException) {
				// Expected - the loop's own (already-cancelled) token is
				// what actually stopped it.
			}
		}

		Status = ServiceStatus.Stopped;
	}

	private async Task RunLoopAsync(
		ServiceDefinition definition,
		TimeSpan interval,
		CancellationToken cancellationToken) {

		while (!cancellationToken.IsCancellationRequested) {
			await RunOnceAsync(definition, cancellationToken);

			try {
				await Task.Delay(interval, cancellationToken);
			}
			catch (OperationCanceledException) {
				return;
			}
		}
	}

	private async Task RunOnceAsync(
		ServiceDefinition definition,
		CancellationToken cancellationToken) {

		log.WriteLine($"[{definition.Name}] running: {definition.Command}");

		try {
			var result = await ShellCommandRunner.RunShellCommandAsync(
				definition.Command,
				definition.Folder,
				line => log.WriteLine($"[{definition.Name}] {line}"),
				line => log.WriteLine($"[{definition.Name}] ERROR: {line}"),
				cancellationToken);

			// Console gets a short summary, not every line of output -
			// this runs unattended and could fire in the middle of the
			// user typing something else. The full detail is in the log.
			Report(definition, $"ran (exit {result.ExitCode})" +
				(result.ExitCode == 0 ? "." : " - see the log for details."));
		}
		catch (OperationCanceledException) {
			throw;
		}
		catch (Exception exception) when (
			exception is IOException or System.ComponentModel.Win32Exception) {

			Report(definition, $"failed to run: {exception.Message}");
		}
	}

	private void Report(ServiceDefinition definition, string message) {
		var line = $"[{definition.Name}] {message}";
		output.WriteLine(line);
		log.WriteLine(line);
	}
}
