using Alexander.Contracts;

namespace Alexander.Core;

public sealed class LifecycleCommandHandlers(ActiveLayerState activeLayer) : ICommandHandler {
	public IReadOnlyList<string> HandledCommands { get; } =
		["/rebuild", "/exit", "/quit"];

	public Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		return command.Text switch {
			"/rebuild" => Task.FromResult(Rebuild(command)),
			"/exit" => Task.FromResult(Exit()),
			"/quit" => Task.FromResult(DtResult.Exit("Closing Alexander.")),
			_ => throw new InvalidOperationException(
				$"LifecycleCommandHandlers cannot handle '{command.Text}'.")
		};
	}

	// One level up: from a service layer back to the shell layer, or
	// from the shell layer out of the app.
	private DtResult Exit() {
		if (activeLayer.Current is { } layer) {
			activeLayer.Current = null;
			return DtResult.Continue($"Left the {layer.Name} layer.");
		}

		return DtResult.Exit("Closing Alexander.");
	}

	private static DtResult Rebuild(ParsedCommand command) {
		if (!command.Flags.ContainsKey("--confirm")) {
			throw new CommandValidationException(
				"This rebuilds Core and reloads it - add --confirm to proceed.");
		}

		return DtResult.Rebuild();
	}
}
