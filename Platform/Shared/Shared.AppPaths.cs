namespace Alexander.Shared;

public sealed class AppPaths {
	// shellBaseDirectory is the Shell exe's own folder (AppContext.BaseDirectory
	// as seen from Shell, passed in by CoreEntryPoint.Create). Core cannot
	// reliably compute this itself: AppContext.BaseDirectory always reports
	// Shell's folder regardless of which assembly asks, and Core.dll is
	// loaded from a byte stream, so its own Assembly.Location is empty.
	public AppPaths(string launchFolder, string shellBaseDirectory) {
		LaunchFolder = launchFolder;
		ShellBaseDirectory = shellBaseDirectory;

		GlobalRoot = Path.Combine(
			Environment.GetFolderPath(
				Environment.SpecialFolder.ApplicationData),
			"Alexander");

		StateFolder = Path.Combine(GlobalRoot, "state");
		Directory.CreateDirectory(StateFolder);
	}

	// The folder Alexander was launched from - the starting point for
	// /cd, before the user moves anywhere else. Not necessarily the
	// CURRENT folder at any later point; see AppState.CurrentFolder for
	// that.
	public string LaunchFolder { get; }
	public string ShellBaseDirectory { get; }

	// App-level state: which folder was current last, logs. Global (one
	// per machine/user), unlike service definitions, which are
	// folder-scoped - see ServicesFolder below.
	public string GlobalRoot { get; }
	public string StateFolder { get; }
	public string StateFile => Path.Combine(StateFolder, "state.json");
	public string LogsFile => Path.Combine(StateFolder, "logs.txt");

	// AI client profiles (provider, model, key) - app-level config, not
	// folder-scoped like a service definition, since the AI is a
	// capability you call from wherever you happen to be, not something
	// tied to one folder's automation.
	public string ClientsFile => Path.Combine(GlobalRoot, "clients.json");

	// Same dual-location pattern the old app used: prefer the file
	// shipped beside the Shell executable, fall back to the launch folder.
	public string ResolveVersionFile() {
		var besideExecutable = Path.Combine(ShellBaseDirectory, "version.xml");

		if (File.Exists(besideExecutable)) {
			return besideExecutable;
		}

		var besideLaunch = Path.Combine(LaunchFolder, "version.xml");

		if (File.Exists(besideLaunch)) {
			return besideLaunch;
		}

		// Neither exists; the caller reports this path as "not found".
		return besideExecutable;
	}

	// Where a given folder's service definitions live - inside THAT
	// folder, not a global registry, so "services can be created in a
	// folder" is literal: the folder itself carries its own automation,
	// portable with it (e.g. checked into source control alongside a
	// project) rather than tracked centrally somewhere else.
	public static string ServicesFolder(string folder) {
		return Path.Combine(folder, ".alexander", "services");
	}
}
