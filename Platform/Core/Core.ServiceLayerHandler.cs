using Alexander.Contracts;

namespace Alexander.Core;

// Adapts one IServiceCommandLayer to the dispatcher's ICommandHandler
// shape. CommandDispatcher creates one per registered layer, so a
// service never registers anything with Core beyond the layer itself.
internal sealed class ServiceLayerHandler(
	IServiceCommandLayer layer,
	ActiveLayerState activeLayer) : ICommandHandler {

	private readonly string prefix = $"/{layer.Name}";

	public IReadOnlyList<string> HandledCommands { get; } = [
		$"/{layer.Name}",
		.. layer.Commands.Select(command => $"/{layer.Name} {command.Text}")
	];

	public Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		// The bare prefix transfers control to the layer.
		if (command.Text == prefix) {
			activeLayer.Current = layer;

			return Task.FromResult(DtResult.Continue(
				$"Entered the {layer.Name} layer. Type its commands without " +
				$"the {prefix} prefix; slash commands still reach the shell " +
				"layer, and /exit returns to it."));
		}

		// "/ai ask" -> "ask": the layer only ever sees its own command text.
		return layer.ExecuteAsync(
			command with { Text = command.Text[(prefix.Length + 1)..] },
			cancellationToken);
	}
}
