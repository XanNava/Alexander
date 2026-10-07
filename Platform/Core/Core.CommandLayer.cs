using Alexander.Contracts;
using Alexander.Services.Automation;
using Alexander.Shared;

namespace Alexander.Core;

// The shell command layer. Implements IDT so the Shell can drive this
// without ever holding a concrete reference to a type from this
// assembly. There's no session - every line is either a known command
// or an error - but control can be transferred to a service command
// layer ("/ai") and back (/exit); see ActiveLayerState.
public sealed class CommandLayer(
	CurrentFolderState folderState,
	IStateStore stateStore,
	AppPaths paths,
	CommandDispatcher dispatcher,
	CommandCatalog commands,
	ActiveLayerState activeLayer,
	IServiceRegistry serviceRegistry,
	IAppLog log) : IDT {

	public string PromptLabel {
		get {
			var trimmed = folderState.Folder.TrimEnd(
				Path.DirectorySeparatorChar,
				Path.AltDirectorySeparatorChar);

			var name = Path.GetFileName(trimmed);

			// A root path (e.g. "C:\" or "/") has no file name - show
			// the whole thing rather than an empty prompt.
			var folder = string.IsNullOrEmpty(name) ? folderState.Folder : name;

			// While a service layer has control, show which - e.g. "src [ai]".
			return activeLayer.Current is { } layer
				? $"{folder} [{layer.Name}]"
				: folder;
		}
	}

	public async Task<string?> StartAsync(CancellationToken cancellationToken) {
		var state = await stateStore.LoadAsync(cancellationToken);

		folderState.Folder =
			!string.IsNullOrWhiteSpace(state.CurrentFolder)
			&& Directory.Exists(state.CurrentFolder)
				? state.CurrentFolder
				: paths.LaunchFolder;

		log.WriteLine("Alexander started.");

		return null;
	}

	// Mirrors HandleInputAsync's routing. Every candidate is a full
	// replacement for rawInput; the caller derives the ghost suffix as
	// candidate[rawInput.Length..].
	public IReadOnlyList<string> GetCompletions(string rawInput) {
		if (string.IsNullOrEmpty(rawInput)) {
			return [];
		}

		var hasSlash = rawInput.StartsWith('/');

		// Inside a service layer, un-slashed input is that layer's own
		// command: complete it as "/ai <input>", then strip the prefix.
		if (activeLayer.Current is { } layer && !hasSlash) {
			var layerPrefix = $"/{layer.Name} ";

			return Complete(layerPrefix + rawInput)
				.Select(candidate => candidate[layerPrefix.Length..])
				.ToArray();
		}

		// At the shell layer the leading slash is optional.
		var normalized = hasSlash ? rawInput : $"/{rawInput}";
		var results = Complete(normalized).ToArray();

		return hasSlash
			? results
			: results.Select(candidate => candidate[1..]).ToArray();
	}

	public async Task<DtResult> HandleInputAsync(
		string input,
		CancellationToken cancellationToken) {

		var trimmed = input.Trim();

		if (trimmed.Length == 0) {
			return DtResult.Continue();
		}

		// Captured before routing - /exit changes it.
		var layer = activeLayer.Current;

		try {
			var result = await RouteAsync(trimmed, layer, cancellationToken);
			log.WriteLine(layer is null ? $"> {trimmed}" : $"[{layer.Name}]> {trimmed}");
			return result;
		}
		catch (CommandValidationException exception) {
			return DtResult.Continue($"Error: {exception.Message}");
		}
		catch (Exception exception) when (
			exception is InvalidOperationException
				or IOException
				or UnauthorizedAccessException
				or HttpRequestException
				or System.ComponentModel.Win32Exception) {

			log.WriteLine($"Error handling '{trimmed}': {exception.Message}");
			return DtResult.Continue($"Error: {exception.Message}");
		}
	}

	public async ValueTask DisposeAsync() {
		// Running services (a timer, a filesystem watcher) live in the
		// same reloadable assembly context as this whole object - they
		// need to be stopped cleanly before that context unloads, not
		// left to whatever happens when their types just vanish.
		await serviceRegistry.StopAllAsync(CancellationToken.None);
	}

	// Slashed input always goes to the shell layer, so /ls, /help and
	// /exit work from anywhere. Un-slashed input goes to the active
	// service layer if there is one; at the shell layer the slash is
	// optional, as before.
	private async Task<DtResult> RouteAsync(
		string input,
		IServiceCommandLayer? layer,
		CancellationToken cancellationToken) {

		var hasSlash = input.StartsWith('/');

		if (layer is null || hasSlash) {
			return await dispatcher.DispatchAsync(
				hasSlash ? input : $"/{input}",
				cancellationToken);
		}

		// The layer's commands go through the dispatcher under their full
		// name, so they get the same flag validation as "/ai ask" typed
		// at the shell layer.
		if (IsLayerCommand(layer, input)) {
			return await dispatcher.DispatchAsync(
				$"/{layer.Name} {input}",
				cancellationToken);
		}

		return await layer.HandleTextAsync(input, cancellationToken)
			?? DtResult.Continue(
				$"Unknown {layer.Name} command. Type /help --section={layer.Name}, " +
				"or /exit to return to the shell layer.");
	}

	private static bool IsLayerCommand(IServiceCommandLayer layer, string input) {
		return layer.Commands.Any(command =>
			input.Equals(command.Text, StringComparison.OrdinalIgnoreCase)
			|| input.StartsWith($"{command.Text} ", StringComparison.OrdinalIgnoreCase));
	}

	// Command names first: if anything in the catalog extends what's been
	// typed, suggest that ("/ai a" -> "/ai ask"). Otherwise, if a full
	// command name and a space have been typed, complete its flags. The
	// longest match wins, so "/ai ask " completes /ai ask's flags rather
	// than /ai's.
	private IEnumerable<string> Complete(string normalized) {
		var byName = commands.NameTrie.FindByPrefix(normalized);

		if (byName.Count > 0) {
			return byName;
		}

		var matched = commands.Definitions
			.Where(definition => normalized.StartsWith(
				$"{definition.Text} ",
				StringComparison.OrdinalIgnoreCase))
			.MaxBy(definition => definition.Text.Length);

		return matched is not null
			? CompleteFlag(normalized, matched)
			: [];
	}

	// currentWord empty (nothing typed yet for this word - right after
	// the command name, or after a completed --flag=value and a space)
	// suggests every not-yet-used flag; a real, non-flag word (a value
	// being typed) suggests nothing, since there's no way to guess a value.
	private static IEnumerable<string> CompleteFlag(
		string normalized,
		CommandDefinition definition) {

		var remainder = normalized[(definition.Text.Length + 1)..];
		var lastSpace = remainder.LastIndexOf(' ');
		var currentWord = lastSpace < 0 ? remainder : remainder[(lastSpace + 1)..];

		if (currentWord.Length > 0
			&& !currentWord.StartsWith("--", StringComparison.Ordinal)) {

			return [];
		}

		var prefix = normalized[..^currentWord.Length];

		var alreadyUsed = remainder[..(lastSpace < 0 ? 0 : lastSpace)]
			.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Select(token => token.Split('=')[0])
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		return definition.Flags
			.Where(flag =>
				flag.Text.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)
				&& !alreadyUsed.Contains(flag.Text))
			.OrderBy(flag => flag.Text, StringComparer.OrdinalIgnoreCase)
			// Boolean flags (--confirm) are normally used bare; anything
			// else takes a value, so the suggestion previews an "="
			// right where you'd type it, e.g. "--path=".
			.Select(flag => prefix + flag.Text + (flag.IsBoolean ? "" : "="));
	}
}
