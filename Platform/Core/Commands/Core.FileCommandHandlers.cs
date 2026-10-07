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

	public IReadOnlyList<string> HandledCommands { get; } =
		["/pwd", "/cd", "/ls", "/cat", "/make", "/mkdir", "/rm", "/run", "/exec"];

	public Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		return command.Text switch {
			"/pwd" => Task.FromResult(PrintCurrentFolder()),
			"/cd" => ChangeFolderAsync(command, cancellationToken),
			"/ls" => Task.FromResult(ListFolder(command)),
			"/cat" => Task.FromResult(ReadFile(command)),
			"/make" => Task.FromResult(MakeFile(command)),
			"/mkdir" => Task.FromResult(MakeFolder(command)),
			"/rm" => Task.FromResult(Remove(command)),
			"/run" => RunFileAsync(command, cancellationToken),
			"/exec" => RunShellCommandAsync(command, cancellationToken),
			_ => throw new InvalidOperationException(
				$"FileCommandHandlers cannot handle '{command.Text}'.")
		};
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
