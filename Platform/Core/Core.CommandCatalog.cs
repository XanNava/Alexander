namespace Alexander.Core;

// ---------------------------------------------------------------------
// Main: the catalog of every command the app understands - the shell
// layer's built-ins plus every registered service command layer.
// ---------------------------------------------------------------------

public sealed class CommandCatalog {
	private static readonly CommandDefinition[] BuiltIn =
	[
		new("/help", "Shows commands and flag descriptions.", [
			new("--section", "Show one section only, e.g. file or ai.")
		]),

		new("/state", "Shows the current folder and running services.", []),

		new("/version", "Shows the app version.", [
			new("--notes", "Include release notes, if any.", IsBoolean: true)
		]),

		new("/pwd", "Prints the current folder.", [], CommandSections.File),

		new("/cd", "Changes the current folder.", [
			new("--path", "Folder to move to; relative to the current folder unless absolute.")
		], CommandSections.File),

		new("/ls", "Lists files and folders.", [
			new("--path", "Folder to list; defaults to the current folder.")
		], CommandSections.File),

		new("/cat", "Prints a file's contents.", [
			new("--path", "File to read.")
		], CommandSections.File),

		new("/make", "Creates a new file.", [
			new("--path", "File to create."),
			new("--content", "Optional initial content; quote values containing spaces.")
		], CommandSections.File),

		new("/mkdir", "Creates a new folder.", [
			new("--path", "Folder to create.")
		], CommandSections.File),

		new("/rm", "Deletes a file or empty folder.", [
			new("--path", "File or folder to delete."),
			new("--confirm", "Required to actually delete.", IsBoolean: true)
		], CommandSections.File),

		new("/run", "Runs a file (script or executable) in the current folder.", [
			new("--path", "File to run."),
			new("--args", "Optional arguments; quote values containing spaces.")
		], CommandSections.File),

		new("/exec", "Runs a system shell command in the current folder.", [
			new("--command", "Command text; quote values containing spaces.")
		], CommandSections.File),

		new("/rebuild", "Rebuilds Core and reloads it without restarting the shell.", [
			new("--confirm", "Required to actually rebuild.", IsBoolean: true)
		], CommandSections.Shell),

		new("/exit", "Leaves the current service layer; at the shell layer, closes Alexander.", [], CommandSections.Shell),
		new("/quit", "Closes Alexander from any layer.", [], CommandSections.Shell)
	];

	public IReadOnlyList<CommandDefinition> Definitions { get; }

	// Every section that has at least one command, in /help order.
	public IReadOnlyList<string> SectionNames { get; }

	// Built once; ConsoleLineReader's completion uses this for the
	// command-name portion of a prefix match instead of scanning
	// Definitions on every keystroke.
	internal CommandTrie NameTrie { get; }

	public CommandCatalog(IEnumerable<IServiceCommandLayer> layers) {
		Definitions = [.. BuiltIn, .. layers.SelectMany(LayerDefinitions)];

		SectionNames = Definitions
			.Select(definition => definition.Section)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		NameTrie = new CommandTrie(Definitions.Select(definition => definition.Text));
	}

	public CommandDefinition? Find(string text) {
		return Definitions.FirstOrDefault(definition =>
			string.Equals(
				definition.Text,
				text,
				StringComparison.OrdinalIgnoreCase));
	}

	// section must be one of SectionNames, or null for everything.
	public string Help(string? section = null) {
		var lines = new List<string>();

		var sections = section is null
			? SectionNames
			: [section];

		foreach (var currentSection in sections) {
			var matching = Definitions
				.Where(definition => string.Equals(
					definition.Section,
					currentSection,
					StringComparison.OrdinalIgnoreCase))
				.ToArray();

			if (matching.Length == 0) {
				continue;
			}

			lines.Add($"-- {currentSection} --");

			foreach (var definition in matching) {
				lines.Add($"{definition.Text}  {definition.Description}");

				foreach (var flag in definition.Flags) {
					lines.Add($"    {flag.Text}  {flag.Description}");
				}
			}

			lines.Add("");
		}

		return string.Join(Environment.NewLine, lines).TrimEnd();
	}

	// A layer contributes "/ai" (which transfers control to it) plus each
	// of its commands under the "/ai" prefix, all in an "ai" section.
	private static IEnumerable<CommandDefinition> LayerDefinitions(IServiceCommandLayer layer) {
		if (string.IsNullOrWhiteSpace(layer.Name)
			|| layer.Name.Any(character => char.IsWhiteSpace(character) || character == '/')) {

			throw new InvalidOperationException(
				$"Service command layer name '{layer.Name}' must be non-empty, " +
				"with no spaces or slashes.");
		}

		var prefix = $"/{layer.Name}";

		yield return new CommandDefinition(
			prefix,
			$"Enters the {layer.Name} layer. {layer.Description}",
			[],
			layer.Name);

		foreach (var command in layer.Commands) {
			yield return command with {
				Text = $"{prefix} {command.Text}",
				Section = layer.Name
			};
		}
	}
}

// ---------------------------------------------------------------------
// Value types.
// ---------------------------------------------------------------------

// Section names for the shell layer's own commands. Each service command
// layer adds one more, named after the layer (e.g. "service", "ai").
//   shell   - reaches out of Core into the Shell process itself
//             (rebuild-and-reload, leaving a layer, or terminating).
//   command - general, top-level app commands (the default).
//   file    - operates on files/folders relative to the current folder.
public static class CommandSections {
	public const string Shell = "shell";
	public const string Command = "command";
	public const string File = "file";
}
