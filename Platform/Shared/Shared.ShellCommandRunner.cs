using System.Diagnostics;

namespace Alexander.Shared;

public sealed record CommandResult(int ExitCode);

// Runs shell commands and files with the OS-appropriate program, output
// streamed live via callbacks rather than buffered until the process
// exits - important for anything long-running (a build, a test run).
// Used both by Core's own /exec and /run commands and by the repeat/watch
// services, so there is exactly one place that knows how to launch a
// process on this machine.
public static class ShellCommandRunner {
	// Runs a raw command STRING via the OS-native shell: PowerShell on
	// Windows (as asked for explicitly), bash on Linux and macOS.
	public static Task<CommandResult> RunShellCommandAsync(
		string command,
		string workingDirectory,
		Action<string> onOutput,
		Action<string> onError,
		CancellationToken cancellationToken) {

		return RunAsync(
			BuildShellStartInfo(command, workingDirectory),
			onOutput,
			onError,
			cancellationToken);
	}

	// Runs a specific FILE. Known script extensions are dispatched to
	// their usual interpreter; anything else (a native executable, or a
	// Unix file with its own shebang and the executable bit set) is run
	// directly.
	public static Task<CommandResult> RunFileAsync(
		string filePath,
		string? arguments,
		string workingDirectory,
		Action<string> onOutput,
		Action<string> onError,
		CancellationToken cancellationToken) {

		return RunAsync(
			BuildFileStartInfo(filePath, arguments, workingDirectory),
			onOutput,
			onError,
			cancellationToken);
	}

	private static ProcessStartInfo BuildShellStartInfo(
		string command,
		string workingDirectory) {

		var startInfo = NewStartInfo(workingDirectory);

		if (OperatingSystem.IsWindows()) {
			startInfo.FileName = "powershell.exe";
			startInfo.ArgumentList.Add("-NoLogo");
			startInfo.ArgumentList.Add("-NoProfile");
			startInfo.ArgumentList.Add("-NonInteractive");
			startInfo.ArgumentList.Add("-Command");
			startInfo.ArgumentList.Add(command);
		} else {
			// Linux and macOS both ship bash; using it directly (rather
			// than $SHELL, which might be something exotic) keeps
			// behavior predictable across environments.
			startInfo.FileName = "/bin/bash";
			startInfo.ArgumentList.Add("-c");
			startInfo.ArgumentList.Add(command);
		}

		return startInfo;
	}

	private static ProcessStartInfo BuildFileStartInfo(
		string filePath,
		string? arguments,
		string workingDirectory) {

		var startInfo = NewStartInfo(workingDirectory);
		var extension = Path.GetExtension(filePath);

		if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)) {
			startInfo.FileName = "powershell.exe";
			startInfo.ArgumentList.Add("-NoLogo");
			startInfo.ArgumentList.Add("-NoProfile");
			startInfo.ArgumentList.Add("-NonInteractive");
			startInfo.ArgumentList.Add("-File");
			startInfo.ArgumentList.Add(filePath);
		} else if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase)) {
			startInfo.FileName = OperatingSystem.IsWindows() ? "python" : "python3";
			startInfo.ArgumentList.Add(filePath);
		} else if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase)) {
			startInfo.FileName = "node";
			startInfo.ArgumentList.Add(filePath);
		} else if (extension.Equals(".sh", StringComparison.OrdinalIgnoreCase)
			&& !OperatingSystem.IsWindows()) {

			startInfo.FileName = "/bin/bash";
			startInfo.ArgumentList.Add(filePath);
		} else {
			// No known interpreter for this extension - run it directly.
			startInfo.FileName = filePath;
		}

		if (!string.IsNullOrWhiteSpace(arguments)) {
			foreach (var argument in SplitArguments(arguments)) {
				startInfo.ArgumentList.Add(argument);
			}
		}

		return startInfo;
	}

	private static ProcessStartInfo NewStartInfo(string workingDirectory) {
		return new ProcessStartInfo {
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
	}

	// Simple whitespace splitting with "quoted sections" support - enough
	// for passing a handful of arguments to /run without needing the
	// full command tokenizer Core's own dispatcher uses for slash
	// commands.
	private static IEnumerable<string> SplitArguments(string arguments) {
		var current = new System.Text.StringBuilder();
		var inQuotes = false;
		var hasToken = false;

		foreach (var character in arguments) {
			if (character == '"') {
				inQuotes = !inQuotes;
				hasToken = true;
			} else if (char.IsWhiteSpace(character) && !inQuotes) {
				if (hasToken) {
					yield return current.ToString();
					current.Clear();
					hasToken = false;
				}
			} else {
				current.Append(character);
				hasToken = true;
			}
		}

		if (hasToken) {
			yield return current.ToString();
		}
	}

	private static async Task<CommandResult> RunAsync(
		ProcessStartInfo startInfo,
		Action<string> onOutput,
		Action<string> onError,
		CancellationToken cancellationToken) {

		using var process = new Process {
			StartInfo = startInfo,
			EnableRaisingEvents = true
		};

		process.OutputDataReceived += (_, args) => {
			if (args.Data is not null) {
				onOutput(args.Data);
			}
		};

		process.ErrorDataReceived += (_, args) => {
			if (args.Data is not null) {
				onError(args.Data);
			}
		};

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		try {
			await process.WaitForExitAsync(cancellationToken);
		}
		catch (OperationCanceledException) {
			try {
				process.Kill(entireProcessTree: true);
			}
			catch (InvalidOperationException) {
				// Already exited between the cancellation and the kill
				// attempt - nothing left to do.
			}

			throw;
		}

		return new CommandResult(process.ExitCode);
	}
}
