using Alexander.Contracts;

namespace Alexander.Core;

public sealed class CommandDispatcher {
	private readonly CommandCatalog catalog;
	private readonly Dictionary<string, ICommandHandler> handlersByCommand;

	public CommandDispatcher(
		CommandCatalog catalog,
		IEnumerable<ICommandHandler> handlers,
		IEnumerable<IServiceCommandLayer> layers,
		ActiveLayerState activeLayer) {

		this.catalog = catalog;
		handlersByCommand = new Dictionary<string, ICommandHandler>(
			StringComparer.OrdinalIgnoreCase);

		// Service command layers don't implement ICommandHandler
		// themselves; each is wrapped here so every command, built-in or
		// service-owned, routes the same way.
		var allHandlers = handlers.Concat(
			layers.Select(layer => (ICommandHandler)new ServiceLayerHandler(layer, activeLayer)));

		foreach (var handler in allHandlers) {
			foreach (var commandText in handler.HandledCommands) {
				if (!handlersByCommand.TryAdd(commandText, handler)) {
					throw new InvalidOperationException(
						$"Two command handlers both claim '{commandText}'.");
				}
			}
		}

		// Catches a catalog entry with no handler at Core load, rather
		// than the first time someone happens to type that exact command.
		var orphaned = catalog.Definitions
			.Select(definition => definition.Text)
			.Where(text => !handlersByCommand.ContainsKey(text))
			.ToArray();

		if (orphaned.Length > 0) {
			throw new InvalidOperationException(
				"CommandCatalog has entries with no registered handler: " +
				string.Join(", ", orphaned));
		}
	}

	public bool IsKnownCommand(string text) {
		return catalog.Find(text) is not null;
	}

	public async Task<DtResult> DispatchAsync(
		string input,
		CancellationToken cancellationToken) {

		var command = ParsedCommand.From(input);
		var definition = catalog.Find(command.Text);

		if (definition is null) {
			return DtResult.Continue("Unknown command. Type /help.");
		}

		ValidateFlags(command, definition);

		var handler = handlersByCommand[definition.Text];

		// Handlers match on exact text, so pass the catalog's spelling
		// rather than whatever casing was typed ("/LS" -> "/ls").
		return await handler.ExecuteAsync(
			command with { Text = definition.Text },
			cancellationToken);
	}

	// A flag the definition doesn't know about, or a bare (valueless)
	// flag the definition hasn't marked IsBoolean, is almost always a
	// typo or a misunderstanding of what the command expects - catching
	// it here means every handler gets this validation for free instead
	// of re-checking it in every ExecuteAsync.
	private static void ValidateFlags(
		ParsedCommand command,
		CommandDefinition definition) {

		var known = definition.Flags.ToDictionary(
			flag => flag.Text,
			StringComparer.OrdinalIgnoreCase);

		foreach (var flagName in command.Flags.Keys) {
			if (!known.TryGetValue(flagName, out var flag)) {
				throw new CommandValidationException(
					$"{definition.Text} does not accept {flagName}.");
			}

			if (command.BareFlags.Contains(flagName) && !flag.IsBoolean) {
				throw new CommandValidationException(
					$"{flagName} requires a value, e.g. {flagName}=...");
			}
		}
	}
}
