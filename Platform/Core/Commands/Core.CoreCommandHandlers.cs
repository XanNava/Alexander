using Alexander.Contracts;
using Alexander.Services.Automation;
using Alexander.Shared;

namespace Alexander.Core;

public sealed class CoreCommandHandlers(
	CommandCatalog catalog,
	CurrentFolderState folderState,
	IServiceRegistry serviceRegistry,
	IVersionStore versionStore,
	IOutputWriter output) : ICommandHandler {

	public IReadOnlyList<string> HandledCommands { get; } =
		["/help", "/state", "/version"];

	public Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		return command.Text switch {
			"/help" => Task.FromResult(PrintHelp(command)),
			"/state" => Task.FromResult(PrintState()),
			"/version" => PrintVersionAsync(command, cancellationToken),
			_ => throw new InvalidOperationException(
				$"CoreCommandHandlers cannot handle '{command.Text}'.")
		};
	}

	private DtResult PrintHelp(ParsedCommand command) {
		string? section = null;

		if (command.Flags.TryGetValue("--section", out var value)) {
			// Sections come from the catalog, so a newly registered
			// service layer is a valid --section with no change here.
			section = catalog.SectionNames.FirstOrDefault(name =>
				string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase))
				?? throw new CommandValidationException(
					$"Unknown section '{value}'. Use " +
					$"{string.Join(", ", catalog.SectionNames)}.");
		}

		output.WriteLine(catalog.Help(section));
		return DtResult.Continue();
	}

	private DtResult PrintState() {
		output.WriteLine($"Current folder: {folderState.Folder}");

		var instances = serviceRegistry.RunningInstances;

		if (instances.Count == 0) {
			output.WriteLine("No services running.");
		} else {
			output.WriteLine($"{instances.Count} service(s) running:");

			foreach (var instance in instances) {
				output.WriteLine(
					$"  {instance.Name} ({instance.TypeName}) - {instance.Folder}");
			}
		}

		return DtResult.Continue();
	}

	private async Task<DtResult> PrintVersionAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var version = await versionStore.LoadAsync(cancellationToken);

		if (!version.Found) {
			output.WriteLine($"No version file found at {version.Path}.");
			return DtResult.Continue();
		}

		output.WriteLine($"Version: {version.Number ?? "(unknown)"}");

		if (command.Flags.ContainsKey("--notes") && !string.IsNullOrWhiteSpace(version.Notes)) {
			output.WriteLine(version.Notes);
		}

		return DtResult.Continue();
	}
}
