using Alexander.Contracts;
using Alexander.Services.Contracts;
using Alexander.Shared;

namespace Alexander.Services.Automation;

// The automation service's command layer: creating, starting, stopping
// and removing folder-worker services (repeat, watch, ...). Named
// "service", so from the shell layer its commands keep their existing
// names ("/service list", "/service create", ...); after "/service"
// transfers control here they're just "list", "create", ...
// Registered by AddAlexanderFolderServices.
public sealed class AutomationCommandLayer(
	ICurrentFolder currentFolder,
	IServiceRegistry serviceRegistry,
	IOutputWriter output) : IServiceCommandLayer {

	private delegate Task<DtResult> Handler(
		AutomationCommandLayer self,
		ParsedCommand command,
		CancellationToken cancellationToken);

	private sealed record Entry(CommandDefinition Definition, Handler Run);

	// Each command's definition sits next to its handler, so the two
	// can't drift apart. Text is relative to the layer - the shell layer
	// adds the "/service" prefix.
	private static readonly Entry[] Entries = [
		new(
			new CommandDefinition("list", "Lists a folder's service definitions and their running status.", [
				new("--path", "Folder to list; defaults to the current folder.")
			]),
			(self, command, ct) => self.ListAsync(command, ct)),

		new(
			new CommandDefinition("create", "Creates and starts a folder-worker service.", [
				new("--name", "Unique name for this service."),
				new("--type", "repeat or watch."),
				new("--command", "Shell command to run when triggered; quote values containing spaces."),
				new("--path", "Folder the service operates on; defaults to the current folder."),
				new("--interval", "repeat only: seconds between runs (default 60)."),
				new("--pattern", "watch only: file glob to match, e.g. *.cs (default: everything)."),
				new("--events", "watch only: comma-separated Changed,Created,Deleted,Renamed (default: all)."),
				new("--subdirs", "watch only: include subfolders.", IsBoolean: true)
			]),
			(self, command, ct) => self.CreateAsync(command, ct)),

		new(
			new CommandDefinition("start", "Starts a previously created (and currently stopped) service.", [
				new("--name", "Service name, as shown by list.")
			]),
			(self, command, ct) => self.StartAsync(command, ct)),

		new(
			new CommandDefinition("stop", "Stops a running service; its definition is kept for a later start.", [
				new("--name", "Service name, as shown by list.")
			]),
			(self, command, ct) => self.StopAsync(command, ct)),

		new(
			new CommandDefinition("remove", "Stops a service (if running) and deletes its definition.", [
				new("--name", "Service name, as shown by list."),
				new("--confirm", "Required to actually delete.", IsBoolean: true)
			]),
			(self, command, ct) => self.RemoveAsync(command, ct))
	];

	private static readonly IReadOnlyDictionary<string, Handler> Handlers =
		Entries.ToDictionary(
			entry => entry.Definition.Text,
			entry => entry.Run,
			StringComparer.OrdinalIgnoreCase);

	public string Name => "service";

	public string Description =>
		"Folder-worker services: repeaters and watchers that run commands for a folder.";

	public IReadOnlyList<CommandDefinition> Commands { get; } =
		[.. Entries.Select(entry => entry.Definition)];

	public Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		return Handlers.TryGetValue(command.Text, out var handler)
			? handler(this, command, cancellationToken)
			: throw new InvalidOperationException(
				$"AutomationCommandLayer cannot handle '{command.Text}'.");
	}

	private async Task<DtResult> ListAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var folder = command.Flags.TryGetValue("--path", out var path)
			? currentFolder.Resolve(path)
			: currentFolder.Folder;

		var definitions = await LoadAllAsync(folder, cancellationToken);

		if (definitions.Count == 0) {
			output.WriteLine($"No service definitions in {folder}.");
		} else {
			output.WriteLine($"{definitions.Count} service definition(s) in {folder}:");

			foreach (var definition in definitions
				.OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)) {

				var status = serviceRegistry.IsRunning(definition.Name) ? "Running" : "Stopped";

				output.WriteLine(
					$"  {definition.Name} ({definition.Type}) - {status} - {definition.Command}");
			}
		}

		if (serviceRegistry.AvailableTypeNames.Count > 0) {
			output.WriteLine();
			output.WriteLine(
				"Available types: " +
				string.Join(
					", ",
					serviceRegistry.AvailableTypeNames.Select(type =>
						$"{type} ({serviceRegistry.DescribeType(type)})")));
		}

		return DtResult.Continue();
	}

	private async Task<DtResult> CreateAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var name = command.Required("--name").Trim();
		ValidateName(name);

		var type = command.Required("--type").Trim().ToLowerInvariant();

		if (!serviceRegistry.AvailableTypeNames.Contains(type, StringComparer.OrdinalIgnoreCase)) {
			throw new CommandValidationException(
				$"Unknown type '{type}'. Available: " +
				$"{string.Join(", ", serviceRegistry.AvailableTypeNames)}.");
		}

		if (serviceRegistry.IsRunning(name)) {
			throw new CommandValidationException(
				$"'{name}' is already running. Use /service stop first to redefine it.");
		}

		var folder = command.Flags.TryGetValue("--path", out var path)
			? currentFolder.Resolve(path)
			: currentFolder.Folder;

		var definition = new ServiceDefinition {
			Name = name,
			Type = type,
			Folder = folder,
			Command = command.Required("--command"),
			IntervalSeconds = command.Flags.TryGetValue("--interval", out var interval)
				? ParseInterval(interval)
				: null,
			WatchPattern = command.Flags.GetValueOrDefault("--pattern"),
			WatchEvents = command.Flags.GetValueOrDefault("--events"),
			IncludeSubdirectories = command.Flags.ContainsKey("--subdirs")
		};

		await SaveAsync(definition, cancellationToken);

		try {
			await serviceRegistry.StartAsync(definition, cancellationToken);
		}
		catch (Exception exception) when (exception is not OperationCanceledException) {
			throw new CommandValidationException(
				$"'{name}' was saved but failed to start: {exception.Message}");
		}

		output.WriteLine($"Created and started '{name}' ({type}) in {folder}.");
		return DtResult.Continue();
	}

	private async Task<DtResult> StartAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var name = command.Required("--name").Trim();

		if (serviceRegistry.IsRunning(name)) {
			output.WriteLine($"'{name}' is already running.");
			return DtResult.Continue();
		}

		var definition = await FindDefinitionAsync(name, cancellationToken)
			?? throw new CommandValidationException(
				$"No saved definition named '{name}'. Use /service create first.");

		try {
			await serviceRegistry.StartAsync(definition, cancellationToken);
		}
		catch (Exception exception) when (exception is not OperationCanceledException) {
			throw new CommandValidationException(
				$"'{name}' failed to start: {exception.Message}");
		}

		output.WriteLine($"Started '{name}'.");
		return DtResult.Continue();
	}

	private async Task<DtResult> StopAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var name = command.Required("--name").Trim();

		if (!serviceRegistry.IsRunning(name)) {
			output.WriteLine($"'{name}' is not running.");
			return DtResult.Continue();
		}

		await serviceRegistry.StopAsync(name, cancellationToken);

		output.WriteLine(
			$"Stopped '{name}'. Its definition is kept - /service start to run it again.");

		return DtResult.Continue();
	}

	private async Task<DtResult> RemoveAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var name = command.Required("--name").Trim();

		if (!command.Flags.ContainsKey("--confirm")) {
			throw new CommandValidationException(
				$"This deletes '{name}''s definition - add --confirm to proceed.");
		}

		if (serviceRegistry.IsRunning(name)) {
			await serviceRegistry.StopAsync(name, cancellationToken);
		}

		var removed = await DeleteDefinitionAsync(name, cancellationToken);

		output.WriteLine(removed
			? $"Removed '{name}'."
			: $"'{name}' was stopped, but no saved definition was found for it.");

		return DtResult.Continue();
	}

	// --- persistence: one JSON file per service, inside the folder it
	// operates on (see Alexander.Platform.Shared.AppPaths.ServicesFolder) --------

	private static async Task<IReadOnlyList<ServiceDefinition>> LoadAllAsync(
		string folder,
		CancellationToken cancellationToken) {

		var directory = AppPaths.ServicesFolder(folder);

		if (!Directory.Exists(directory)) {
			return [];
		}

		var definitions = new List<ServiceDefinition>();

		foreach (var file in Directory.EnumerateFiles(directory, "*.json")) {
			var definition = await JsonFiles.ReadAsync<ServiceDefinition>(
				file,
				cancellationToken);

			if (definition is not null) {
				definitions.Add(definition);
			}
		}

		return definitions;
	}

	// A name is only ever looked up relative to the current folder - the
	// same folder list shows by default, and where create saves to
	// unless --path pointed it somewhere else.
	private async Task<ServiceDefinition?> FindDefinitionAsync(
		string name,
		CancellationToken cancellationToken) {

		var definitions = await LoadAllAsync(currentFolder.Folder, cancellationToken);

		return definitions.FirstOrDefault(definition =>
			string.Equals(definition.Name, name, StringComparison.OrdinalIgnoreCase));
	}

	private static Task SaveAsync(
		ServiceDefinition definition,
		CancellationToken cancellationToken) {

		var path = Path.Combine(
			AppPaths.ServicesFolder(definition.Folder),
			$"{JsonFiles.SafeFileName(definition.Name)}.json");

		return JsonFiles.WriteAsync(path, definition, cancellationToken);
	}

	private async Task<bool> DeleteDefinitionAsync(
		string name,
		CancellationToken cancellationToken) {

		var definition = await FindDefinitionAsync(name, cancellationToken);

		if (definition is null) {
			return false;
		}

		var path = Path.Combine(
			AppPaths.ServicesFolder(definition.Folder),
			$"{JsonFiles.SafeFileName(name)}.json");

		if (!File.Exists(path)) {
			return false;
		}

		File.Delete(path);
		return true;
	}

	private static void ValidateName(string name) {
		if (name.Length is < 1 or > 80
			|| name.Any(character =>
				!char.IsLetterOrDigit(character)
				&& character is not ('-' or '_' or '.'))) {

			throw new CommandValidationException(
				"Service names must contain 1-80 letters, digits, dots, " +
				"hyphens, or underscores.");
		}
	}

	private static int ParseInterval(string value) {
		if (!int.TryParse(value, out var seconds) || seconds < 1) {
			throw new CommandValidationException(
				"--interval must be a whole number of seconds, at least 1.");
		}

		return seconds;
	}
}
