using Alexander.Contracts;
using Alexander.Shared;

namespace Alexander.Core;

public sealed class FileCommandHandlers(
	CurrentFolderState folderState,
	IStateStore stateStore,
	IOutputWriter output) : ICommandHandler {

	// A file this size or larger isn't something /cat should dump raw
	// into the terminal - almost certainly not what was meant, and could
	// be binary.
	private const long MaxCatBytes = 1_000_000;

	private delegate Task<DtResult> Handler(
		FileCommandHandlers self,
		ParsedCommand command,
		CancellationToken cancellationToken);

	// Single source of truth for which commands this class handles and
	// how each one is dispatched. Static so it's built once per type;
	// handlers receive the instance as `self`.
	private static readonly IReadOnlyDictionary<string, Handler> Registry =
		new Dictionary<string, Handler>(StringComparer.Ordinal) {
			["/pwd"] = Sync((self, _) => self.PrintCurrentFolder()),
			["/cd"] = (self, command, ct) => self.ChangeFolderAsync(command, ct),
			["/ls"] = Sync((self, command) => self.ListFolder(command)),
			["/cat"] = Sync((self, command) => self.ReadFile(command)),
			["/make"] = Sync((self, command) => self.MakeFile(command)),
			["/mkdir"] = Sync((self, command) => self.MakeFolder(command)),
			["/rm"] = Sync((self, command) => self.Remove(command)),
			["/run"] = (self, command, ct) => self.RunFileAsync(command, ct),
			["/exec"] = (self, command, ct) => self.RunShellCommandAsync(command, ct),
		};

	// Adapts a synchronous handler to the async Handler shape.
	private static Handler Sync(Func<FileCommandHandlers, ParsedCommand, DtResult> handler) =>
		(self, command, _) => Task.FromResult(handler(self, command));

	public IReadOnlyList<string> HandledCommands { get; } = [.. Registry.Keys];

	public Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		return Registry.TryGetValue(command.Text, out var handler)
			? handler(this, command, cancellationToken)
			: throw new InvalidOperationException(
				$"FileCommandHandlers cannot handle '{command.Text}'.");
	}

	private DtResult PrintCurrentFolder() {
		output.WriteLine(folderState.Folder);
		return DtResult.Continue();
	}

	private async Task<DtResult> ChangeFolderAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var target = ResolveAnywhere(command.Required("--path"));

		if (!Directory.Exists(target)) {
			throw new CommandValidationException($"No such folder: {target}");
		}

		folderState.Folder = target;

		var state = await stateStore.LoadAsync(cancellationToken);
		state.CurrentFolder = target;
		await stateStore.SaveAsync(state, cancellationToken);

		output.WriteLine(target);
		return DtResult.Continue();
	}

	private DtResult ListFolder(ParsedCommand command) {
		var target = command.Flags.TryGetValue("--path", out var path)
			? ResolveAnywhere(path)
			: folderState.Folder;

		if (!Directory.Exists(target)) {
			throw new CommandValidationException($"No such folder: {target}");
		}

		var entries = new List<string>();

		foreach (var directory in Directory
			.EnumerateDirectories(target)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) {

			entries.Add($"  {Path.GetFileName(directory)}/");
		}

		foreach (var file in Directory
			.EnumerateFiles(target)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) {

			entries.Add($"  {Path.GetFileName(file)}");
		}

		output.WriteLine(entries.Count == 0
			? $"{target} is empty."
			: $"{target}:{Environment.NewLine}{string.Join(Environment.NewLine, entries)}");

		return DtResult.Continue();
	}

	private DtResult ReadFile(ParsedCommand command) {
		var target = ResolveAnywhere(command.Required("--path"));

		if (!File.Exists(target)) {
			throw new CommandValidationException($"No such file: {target}");
		}

		var length = new FileInfo(target).Length;

		if (length > MaxCatBytes) {
			throw new CommandValidationException(
				$"{target} is {length:N0} bytes - too large to print. " +
				"Use /run or /exec to process it instead.");
		}

		output.WriteLine(File.ReadAllText(target));
		return DtResult.Continue();
	}

	private DtResult MakeFile(ParsedCommand command) {
		var target = ResolveWithinCurrentFolder(command.Required("--path"));

		if (File.Exists(target)) {
			throw new CommandValidationException(
				$"{target} already exists. Delete it first (/rm) or choose a different path.");
		}

		var directory = Path.GetDirectoryName(target);

		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(
			target,
			command.Flags.TryGetValue("--content", out var content) ? content : "");

		output.WriteLine($"Created {target}.");
		return DtResult.Continue();
	}

	private DtResult MakeFolder(ParsedCommand command) {
		var target = ResolveWithinCurrentFolder(command.Required("--path"));

		Directory.CreateDirectory(target);

		output.WriteLine($"Created {target}.");
		return DtResult.Continue();
	}

	private DtResult Remove(ParsedCommand command) {
		var target = ResolveWithinCurrentFolder(command.Required("--path"));

		if (!command.Flags.ContainsKey("--confirm")) {
			throw new CommandValidationException(
				$"This deletes {target} - add --confirm to proceed.");
		}

		if (File.Exists(target)) {
			File.Delete(target);
			output.WriteLine($"Deleted {target}.");
			return DtResult.Continue();
		}

		if (Directory.Exists(target)) {
			if (Directory.EnumerateFileSystemEntries(target).Any()) {
				throw new CommandValidationException(
					$"{target} is not empty. /rm only deletes empty folders.");
			}

			Directory.Delete(target);
			output.WriteLine($"Deleted {target}.");
			return DtResult.Continue();
		}

		throw new CommandValidationException($"No such file or folder: {target}");
	}

	private async Task<DtResult> RunFileAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var target = ResolveAnywhere(command.Required("--path"));

		if (!File.Exists(target)) {
			throw new CommandValidationException($"No such file: {target}");
		}

		command.Flags.TryGetValue("--args", out var arguments);

		try {
			var result = await ShellCommandRunner.RunFileAsync(
				target,
				arguments,
				folderState.Folder,
				line => output.WriteLine(line),
				line => output.WriteError(line),
				cancellationToken);

			output.WriteLine($"Exit code: {result.ExitCode}");
		}
		catch (System.ComponentModel.Win32Exception exception) {
			throw new CommandValidationException(
				$"Could not run {target}: {exception.Message}");
		}

		return DtResult.Continue();
	}

	private async Task<DtResult> RunShellCommandAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var text = command.Required("--command");

		try {
			var result = await ShellCommandRunner.RunShellCommandAsync(
				text,
				folderState.Folder,
				line => output.WriteLine(line),
				line => output.WriteError(line),
				cancellationToken);

			output.WriteLine($"Exit code: {result.ExitCode}");
		}
		catch (System.ComponentModel.Win32Exception exception) {
			throw new CommandValidationException(
				$"Could not run a shell: {exception.Message}");
		}

		return DtResult.Continue();
	}

	// For read/run: any path is fine, relative to the current folder
	// unless already absolute.
	private string ResolveAnywhere(string path) {
		return Path.GetFullPath(
			Path.IsPathRooted(path) ? path : Path.Combine(folderState.Folder, path));
	}

	// For create/delete: the resolved path must stay within the current
	// folder's own tree - a reasonable default for a tool whose whole
	// premise is "operates on the current folder," and cheap insurance
	// against a typo'd --path taking out something far away (e.g. --path=/
	// with --confirm).
	private string ResolveWithinCurrentFolder(string path) {
		var resolved = ResolveAnywhere(path);
		var root = Path.GetFullPath(folderState.Folder)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		if (resolved != root
			&& !resolved.StartsWith(
				root + Path.DirectorySeparatorChar,
				StringComparison.OrdinalIgnoreCase)) {

			throw new CommandValidationException(
				$"{resolved} is outside the current folder ({folderState.Folder}). " +
				"/cd there first.");
		}

		return resolved;
	}
}
