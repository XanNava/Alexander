using System.Runtime.Versioning;

using Alexander.Contracts;
using Alexander.Services.Contracts;
using Alexander.Shared;

namespace Alexander.Services.Explorer;

// The Explorer service's command layer: the standalone app's CLI
// (open, list, group add/remove/list, default, serve/stop) as Alexander
// commands. From the shell layer they're "/explorer open ...", etc.;
// after "/explorer" transfers control here they're "open ...", and any
// other text is treated as a folder to open.
[SupportedOSPlatform("windows")]
internal sealed class ExplorerCommandLayer(
	ExplorerHost host,
	ICurrentFolder currentFolder,
	IOutputWriter output) : IServiceCommandLayer {

	private delegate Task<DtResult> Handler(
		ExplorerCommandLayer self,
		ParsedCommand command,
		CancellationToken cancellationToken);

	private sealed record Entry(CommandDefinition Definition, Handler Run);

	private const string ModeHelp = "tab, navigate, or new_window";

	// Each command's definition sits next to its handler, so the two
	// can't drift apart. Text is relative to the layer - the shell layer
	// adds the "/explorer" prefix.
	private static readonly Entry[] Entries = [
		new(
			new CommandDefinition("open", "Opens a folder in Explorer according to the matching rule.", [
				new("--path", "Folder to open; relative to the current folder. Defaults to the current folder."),
				new("--mode", $"{ModeHelp}; overrides the rule for this open only.")
			]),
			(self, command, ct) => self.OpenAsync(command, ct)),

		new(
			new CommandDefinition("list", "Lists open Explorer windows and their tabs.", []),
			(self, _, ct) => self.ListAsync(ct)),

		new(
			new CommandDefinition("groups", "Shows the default rule and every group rule.", []),
			(self, _, _) => Task.FromResult(self.ShowGroups())),

		new(
			new CommandDefinition("group add", "Creates or replaces a group: folders under a root that open together.", [
				new("--name", "Group name."),
				new("--root", "Folder the group covers; relative to the current folder unless absolute."),
				new("--mode", $"{ModeHelp} (default tab)."),
				new("--focus-if-open", "Focus a tab already showing the folder.", IsBoolean: true),
				new("--no-focus-if-open", "Always open, even if a tab already shows the folder.", IsBoolean: true)
			]),
			(self, command, _) => Task.FromResult(self.AddGroup(command))),

		new(
			new CommandDefinition("group remove", "Deletes a group rule.", [
				new("--name", "Group name, as shown by groups.")
			]),
			(self, command, _) => Task.FromResult(self.RemoveGroup(command))),

		new(
			new CommandDefinition("default", "Shows or changes the rule for folders outside every group.", [
				new("--mode", ModeHelp),
				new("--focus-if-open", "Focus a tab already showing the folder.", IsBoolean: true),
				new("--no-focus-if-open", "Always open, even if a tab already shows the folder.", IsBoolean: true),
				new("--avoid-group-windows", "Keep ungrouped folders out of group windows.", IsBoolean: true),
				new("--no-avoid-group-windows", "Allow ungrouped folders into group windows.", IsBoolean: true)
			]),
			(self, command, _) => Task.FromResult(self.SetDefault(command))),

		new(
			new CommandDefinition("track", "Shows or changes focus tracking, which keeps \"last used window\" accurate.", [
				new("--on", "Start tracking which Explorer window you use.", IsBoolean: true),
				new("--off", "Stop tracking.", IsBoolean: true)
			]),
			(self, command, _) => Task.FromResult(self.Track(command)))
	];

	private static readonly IReadOnlyDictionary<string, Handler> Handlers =
		Entries.ToDictionary(
			entry => entry.Definition.Text,
			entry => entry.Run,
			StringComparer.OrdinalIgnoreCase);

	public string Name => "explorer";

	public string Description => "Opens folders in Explorer windows and tabs by per-folder rules.";

	public IReadOnlyList<CommandDefinition> Commands { get; } =
		[.. Entries.Select(entry => entry.Definition)];

	public Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		return Handlers.TryGetValue(command.Text, out var handler)
			? handler(this, command, cancellationToken)
			: throw new InvalidOperationException(
				$"ExplorerCommandLayer cannot handle '{command.Text}'.");
	}

	// Inside the layer, anything that isn't a command is a folder to
	// open: "src [explorer]> C:\Projects\My App" just works, spaces and
	// all, since free text skips the flag parser.
	public async Task<DtResult?> HandleTextAsync(
		string text,
		CancellationToken cancellationToken) {

		return await OpenPathAsync(ResolvePath(text), null, cancellationToken);
	}

	// --- commands --------------------------------------------------------

	private Task<DtResult> OpenAsync(
		ParsedCommand command,
		CancellationToken cancellationToken) {

		var path = command.Flags.TryGetValue("--path", out var raw)
			? ResolvePath(raw)
			: currentFolder.Folder;

		var mode = command.Flags.TryGetValue("--mode", out var modeText)
			? ParseMode(modeText)
			: (OpenMode?)null;

		return OpenPathAsync(path, mode, cancellationToken);
	}

	private async Task<DtResult> OpenPathAsync(
		string path,
		OpenMode? mode,
		CancellationToken cancellationToken) {

		// A file opens its containing folder, as in the standalone app.
		if (File.Exists(path)) {
			path = Path.GetDirectoryName(path)!;
		}

		if (!Directory.Exists(path)) {
			throw new CommandValidationException($"Not a folder: {path}");
		}

		var result = await host.RunAsync(
			manager => manager.Open(path, mode),
			cancellationToken);

		var what = result.Action switch {
			"focused_existing" => "Focused the tab already showing",
			"new_tab" => "Opened in a new tab:",
			"navigated" => "Navigated a tab to",
			_ when result.Hwnd == 0 => "Launched Explorer (window not detected yet):",
			_ => "Opened a new window:"
		};

		output.WriteLine(
			$"{what} {result.Path}" +
			(result.Group is null ? "" : $"  (group: {result.Group})"));

		return DtResult.Continue();
	}

	private async Task<DtResult> ListAsync(CancellationToken cancellationToken) {
		var windows = await host.RunAsync(manager => manager.List(), cancellationToken);

		if (windows.Count == 0) {
			output.WriteLine("No Explorer windows open.");
			return DtResult.Continue();
		}

		foreach (var window in windows) {
			output.WriteLine($"[{window.Hwnd}] {window.Title}");

			foreach (var tab in window.Tabs) {
				output.WriteLine(
					$"   {(tab.Active ? '*' : ' ')} {tab.Path}" +
					(tab.Group is null ? "" : $"  (group: {tab.Group})"));
			}
		}

		return DtResult.Continue();
	}

	private DtResult ShowGroups() {
		var config = ExplorerStore.LoadConfig();
		var rule = config.Default;

		output.WriteLine(
			$"default: mode={ModeName(rule.Mode)} focus_if_open={rule.FocusIfOpen} " +
			$"avoid_group_windows={rule.AvoidGroupWindows}");

		if (config.Groups.Count == 0) {
			output.WriteLine("No groups. Use group add to create one.");
		}

		foreach (var group in config.Groups) {
			output.WriteLine(
				$"{group.Name}: {group.Root}  mode={ModeName(group.Mode ?? rule.Mode)} " +
				$"focus_if_open={group.FocusIfOpen ?? rule.FocusIfOpen}");
		}

		return DtResult.Continue();
	}

	private DtResult AddGroup(ParsedCommand command) {
		var name = command.Required("--name").Trim();
		var root = ResolvePath(command.Required("--root"));

		var mode = command.Flags.TryGetValue("--mode", out var modeText)
			? ParseMode(modeText)
			: OpenMode.Tab;

		var group = new GroupRule {
			Name = name,
			Root = root,
			Mode = mode,
			FocusIfOpen = ReadBool(command, "focus-if-open")
		};

		var config = ExplorerStore.LoadConfig();
		config.Groups.RemoveAll(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
		config.Groups.Add(group);
		ExplorerStore.Save(ExplorerStore.ConfigFile, config);

		output.WriteLine($"Saved group '{name}' ({root}) to {ExplorerStore.ConfigFile}.");
		return DtResult.Continue();
	}

	private DtResult RemoveGroup(ParsedCommand command) {
		var name = command.Required("--name").Trim();
		var config = ExplorerStore.LoadConfig();

		if (config.Groups.RemoveAll(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) {
			throw new CommandValidationException($"No group named '{name}'. Use groups to list them.");
		}

		ExplorerStore.Save(ExplorerStore.ConfigFile, config);

		output.WriteLine($"Removed group '{name}'.");
		return DtResult.Continue();
	}

	private DtResult SetDefault(ParsedCommand command) {
		var config = ExplorerStore.LoadConfig();
		var changed = false;

		if (command.Flags.TryGetValue("--mode", out var modeText)) {
			config.Default.Mode = ParseMode(modeText);
			changed = true;
		}

		if (ReadBool(command, "focus-if-open") is { } focus) {
			config.Default.FocusIfOpen = focus;
			changed = true;
		}

		if (ReadBool(command, "avoid-group-windows") is { } avoid) {
			config.Default.AvoidGroupWindows = avoid;
			changed = true;
		}

		if (changed) {
			ExplorerStore.Save(ExplorerStore.ConfigFile, config);
		}

		var rule = config.Default;

		output.WriteLine(
			$"{(changed ? "Saved default" : "Default")}: mode={ModeName(rule.Mode)} " +
			$"focus_if_open={rule.FocusIfOpen} avoid_group_windows={rule.AvoidGroupWindows}");

		return DtResult.Continue();
	}

	private DtResult Track(ParsedCommand command) {
		var on = command.Flags.ContainsKey("--on");
		var off = command.Flags.ContainsKey("--off");

		if (on && off) {
			throw new CommandValidationException("Use either --on or --off, not both.");
		}

		if (on) {
			host.StartTracking();
		} else if (off) {
			host.StopTracking();
		}

		output.WriteLine(host.IsTracking ? "Focus tracking is on." : "Focus tracking is off.");
		return DtResult.Continue();
	}

	// --- helpers ---------------------------------------------------------

	// Relative to Alexander's current folder (not the process's working
	// directory, which is what the standalone app used), with %VARS%
	// expanded and surrounding quotes dropped.
	private string ResolvePath(string raw) {
		try {
			return currentFolder.Resolve(
				Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException) {
			throw new CommandValidationException($"Bad path '{raw}': {exception.Message}");
		}
	}

	// --name -> true, --no-name -> false, neither -> null. A value is also
	// accepted on the positive form (--name=false).
	private static bool? ReadBool(ParsedCommand command, string name) {
		bool? value = null;

		if (command.Flags.TryGetValue($"--{name}", out var raw)) {
			value = bool.TryParse(raw, out var parsed)
				? parsed
				: throw new CommandValidationException($"--{name} takes true or false.");
		}

		if (command.Flags.ContainsKey($"--no-{name}")) {
			if (value is not null) {
				throw new CommandValidationException(
					$"Use either --{name} or --no-{name}, not both.");
			}

			value = false;
		}

		return value;
	}

	private static OpenMode ParseMode(string value) {
		return value.Trim().ToLowerInvariant() switch {
			"tab" => OpenMode.Tab,
			"navigate" => OpenMode.Navigate,
			"new_window" or "window" => OpenMode.NewWindow,
			_ => throw new CommandValidationException($"Unknown mode '{value}'. Use {ModeHelp}.")
		};
	}

	private static string ModeName(OpenMode mode) {
		return mode switch {
			OpenMode.Tab => "tab",
			OpenMode.Navigate => "navigate",
			_ => "new_window"
		};
	}
}
