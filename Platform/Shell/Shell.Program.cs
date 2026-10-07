using System.Diagnostics;
using System.Reflection;

using Alexander.Contracts;
using Alexander.Shell;

using var cancellationSource = new CancellationTokenSource();

Console.CancelKeyPress += (_, args) => {
	args.Cancel = true;
	cancellationSource.Cancel();
};

var launchFolder = Environment.CurrentDirectory;
var shellBaseDirectory = AppContext.BaseDirectory;
var solutionRoot = FindSolutionRoot(shellBaseDirectory);
var coreProjectPath = Path.Combine(
	solutionRoot,
	"Platform",
	"Core",
	"Alexander.Core.csproj");

// NOTE: assumes Core was built in the same configuration/TFM as Shell.
// Fine for local dev; parameterize if you ever publish these separately.
#if DEBUG
const string configuration = "Debug";
#else
const string configuration = "Release";
#endif

var coreAssemblyPath = Path.Combine(
	solutionRoot,
	"Platform",
	"Core",
	"bin",
	configuration,
	"net8.0",
	"Alexander.Core.dll");

// Shell deliberately has no project reference to Core (that's what lets
// Core be rebuilt independently), which means an IDE's "build the startup
// project" won't build Core for you. The very first run - or any run after
// a manual `git clean`/`rm -rf bin obj` - would otherwise crash with a
// confusing "Failed to locate managed application" error. Build it here
// instead of assuming it already exists.
if (!File.Exists(coreAssemblyPath)) {
	Console.WriteLine(
		"Alexander.Core.dll was not found; building Core for the first time...");
	Console.WriteLine();

	if (!await RunDotnetBuildAsync(
		coreProjectPath,
		launchFolder,
		cancellationSource.Token)) {

		Console.WriteLine(
			"Core failed to build. Fix the errors above, then run again.");
		return;
	}

	Console.WriteLine();
}

var host = new CoreHost();

await host.LoadAsync(
	coreAssemblyPath,
	launchFolder,
	shellBaseDirectory,
	cancellationSource.Token);

Console.WriteLine("Alexander - a folder worker");
Console.WriteLine(
	"/help: commands | /state: current folder + running services | " +
	"/exit or /quit: close Alexander | /rebuild --confirm: rebuild + reload Core");
Console.WriteLine(
	"Tab/Right: complete one word at a time | Up/Down/Shift+Tab: cycle | Shift+Space: accept all");
Console.WriteLine();

await PrintStartupMessageAsync(host, cancellationSource.Token);

try {
	while (!cancellationSource.IsCancellationRequested) {
		Console.ForegroundColor = ConsoleColor.Cyan;
		Console.Write($"{host.Current.PromptLabel} > ");
		Console.ResetColor();

		string? input;

		try {
			// A lambda, not a bound method group: host.Current can change
			// (a /rebuild swaps in a new Core generation) between one
			// keystroke and the next in principle, so this re-resolves it
			// on every call rather than completing against whichever
			// instance happened to be current when the delegate was made.
			// Task.Run + WaitAsync keeps the same "abandon the blocked
			// call on Ctrl+C" behavior as before: the background thread
			// may stay parked in Console.ReadKey, but the shell loop
			// itself is free to unwind.
			input = await Task.Run(
				() => ConsoleLineReader.ReadLine(
					line => host.Current.GetCompletions(line)),
				CancellationToken.None).WaitAsync(cancellationSource.Token);
		}
		catch (OperationCanceledException)
			when (cancellationSource.IsCancellationRequested) {
			break;
		}

		if (input is null) {
			// Redirected input closed (EOF) - ConsoleLineReader's own
			// fallback path is the only way this happens.
			break;
		}

		DtResult result;

		try {
			result = await host.Current.HandleInputAsync(
				input,
				cancellationSource.Token);
		}
		catch (OperationCanceledException)
			when (cancellationSource.IsCancellationRequested) {
			break;
		}

		if (result.Message is not null) {
			Console.WriteLine(result.Message);
			Console.WriteLine();
		}

		switch (result.Action) {
		case DtAction.Exit:
			return;

		case DtAction.Rebuild:
			var reloaded = await RebuildAsync(
				host,
				coreProjectPath,
				coreAssemblyPath,
				launchFolder,
				shellBaseDirectory,
				cancellationSource.Token);

			if (reloaded) {
				await PrintStartupMessageAsync(host, cancellationSource.Token);
			}

			break;
		}
	}
}
finally {
	await host.DisposeAsync();
}

return;

static async Task PrintStartupMessageAsync(
	CoreHost host,
	CancellationToken cancellationToken) {

	var message = await host.Current.StartAsync(cancellationToken);

	if (message is not null) {
		Console.WriteLine(message);
		Console.WriteLine();
	}
}

static async Task<bool> RebuildAsync(
	CoreHost host,
	string coreProjectPath,
	string coreAssemblyPath,
	string launchFolder,
	string shellBaseDirectory,
	CancellationToken cancellationToken) {

	if (!await RunDotnetBuildAsync(coreProjectPath, launchFolder, cancellationToken)) {
		Console.WriteLine(
			"Build failed; Core was NOT reloaded. " +
			"Fix the errors above and run /rebuild --confirm again.");
		Console.WriteLine();
		return false;
	}

	Console.WriteLine("Build succeeded. Reloading Core...");

	try {
		await host.SwapAsync(
			coreAssemblyPath,
			launchFolder,
			shellBaseDirectory,
			cancellationToken);
	}
	catch (Exception exception) when (
		exception is IOException
			or UnauthorizedAccessException
			or ReflectionTypeLoadException
			or TargetInvocationException) {

		Console.WriteLine(
			$"Error: the new Core build could not be loaded " +
			$"({exception.GetType().Name}). The previous Core is still running.");
		Console.WriteLine();
		return false;
	}

	Console.WriteLine("Core reloaded.");
	Console.WriteLine();

	// Any folder-worker services the previous generation had running are
	// stopped as part of the swap (see CoreHost.SwapAsync) - their
	// definitions are saved to disk, so /service list shows what to
	// restart with /service start. The current folder itself is re-read
	// from disk by the new generation's StartAsync below, so it carries
	// over even though nothing about it is passed explicitly here.
	return true;
}

// Shared by the startup auto-build and /rebuild --confirm. Returns whether
// the build succeeded; all output is streamed straight to the console.
static async Task<bool> RunDotnetBuildAsync(
	string coreProjectPath,
	string launchFolder,
	CancellationToken cancellationToken) {

	if (!File.Exists(coreProjectPath)) {
		Console.WriteLine($"Error: Core project not found at {coreProjectPath}.");
		Console.WriteLine();
		return false;
	}

	Console.WriteLine("Running 'dotnet build' on Core...");
	Console.WriteLine();

	var startInfo = new ProcessStartInfo {
		FileName = "dotnet",
		WorkingDirectory = launchFolder,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		UseShellExecute = false
	};

	startInfo.ArgumentList.Add("build");
	startInfo.ArgumentList.Add(coreProjectPath);

	using var process = new Process { StartInfo = startInfo };

	process.OutputDataReceived += (_, args) => {
		if (args.Data is not null) {
			Console.WriteLine(args.Data);
		}
	};

	process.ErrorDataReceived += (_, args) => {
		if (args.Data is not null) {
			Console.Error.WriteLine(args.Data);
		}
	};

	try {
		if (!process.Start()) {
			Console.WriteLine("Error: could not start 'dotnet build'.");
			Console.WriteLine();
			return false;
		}
	}
	catch (System.ComponentModel.Win32Exception exception) {
		Console.WriteLine(
			$"Error: could not start 'dotnet build' ({exception.Message}). " +
			"Is the .NET SDK installed and on PATH?");
		Console.WriteLine();
		return false;
	}

	process.BeginOutputReadLine();
	process.BeginErrorReadLine();

	await process.WaitForExitAsync(cancellationToken);
	Console.WriteLine();

	return process.ExitCode == 0;
}

static string FindSolutionRoot(string startingDirectory) {
	var directory = new DirectoryInfo(startingDirectory);

	while (directory is not null && directory.GetFiles("*.sln").Length == 0) {
		directory = directory.Parent;
	}

	return directory?.FullName
		?? throw new InvalidOperationException(
			"Could not locate the solution (*.sln) above " +
			$"'{startingDirectory}'.");
}
