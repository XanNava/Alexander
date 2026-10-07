using System.Text.Json;
using System.Text.Json.Serialization;

namespace Alexander.Services.Explorer;

// Rules for where a folder opens, the state remembered between runs, and
// where both are stored. Unchanged from the standalone ExplorerManager
// apart from names (Config -> ExplorerConfig, ...) and logging, which now
// goes to Alexander's app log instead of manager.log.

internal enum OpenMode { Tab, Navigate, NewWindow }

internal sealed class DefaultRule {
	public OpenMode Mode { get; set; } = OpenMode.NewWindow;

	// If the folder is already open in some tab, bring that tab forward instead.
	public bool FocusIfOpen { get; set; } = true;

	// Don't drop ungrouped folders into a window that belongs to a group.
	public bool AvoidGroupWindows { get; set; } = true;
}

internal sealed class GroupRule {
	public string Name { get; set; } = "";
	public string Root { get; set; } = "";
	public OpenMode? Mode { get; set; }
	public bool? FocusIfOpen { get; set; }
}

internal sealed class ExplorerConfig {
	public DefaultRule Default { get; set; } = new();
	public List<GroupRule> Groups { get; set; } = new();

	// Longest matching group root wins, so nested groups are allowed.
	public Policy Resolve(string pathKey) {
		GroupRule? best = null;
		var bestRoot = "";

		foreach (var group in Groups) {
			var rootKey = PathKey.Norm(group.Root);

			if (pathKey.Length > 0
				&& PathKey.IsUnder(pathKey, rootKey)
				&& rootKey.Length > bestRoot.Length) {

				best = group;
				bestRoot = rootKey;
			}
		}

		return best is not null
			? new Policy(
				best.Name,
				bestRoot,
				best.Mode ?? Default.Mode,
				best.FocusIfOpen ?? Default.FocusIfOpen,
				AvoidGroupWindows: false)
			: new Policy(
				null,
				null,
				Default.Mode,
				Default.FocusIfOpen,
				Default.AvoidGroupWindows);
	}

	public bool InAnyGroup(string pathKey) {
		return pathKey.Length > 0
			&& Groups.Any(group => PathKey.IsUnder(pathKey, PathKey.Norm(group.Root)));
	}
}

internal sealed record Policy(
	string? Group,
	string? Root,
	OpenMode Mode,
	bool FocusIfOpen,
	bool AvoidGroupWindows);

// Things remembered between runs: last window used per group, and window
// MRU order.
internal sealed class ExplorerState {
	public Dictionary<string, long> LastWindow { get; set; } = new();
	public List<long> Mru { get; set; } = new();
}

internal static class PathKey {
	// Comparison key: absolute, lower-case, no trailing backslash.
	public static string Norm(string? path) {
		if (string.IsNullOrEmpty(path)) {
			return "";
		}

		// Shell namespace (This PC, etc.).
		if (path.StartsWith("::", StringComparison.Ordinal)) {
			return path.ToLowerInvariant();
		}

		try {
			return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant();
		}
		catch {
			return path.ToLowerInvariant();
		}
	}

	public static bool IsUnder(string child, string root) {
		return child == root
			|| child.StartsWith(root + "\\", StringComparison.Ordinal);
	}
}

internal static class ExplorerStore {
	// Same folder the standalone ExplorerManager used, so an existing
	// config.json and state.json carry over unchanged.
	public static readonly string AppDir = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
		"ExplorerManager");

	public static string ConfigFile => Path.Combine(AppDir, "config.json");
	public static string StateFile => Path.Combine(AppDir, "state.json");

	public static readonly JsonSerializerOptions Json = new() {
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
	};

	// Set once by ExplorerHost to Alexander's IAppLog. Messages are
	// dropped until then - logging must never crash anything.
	public static Action<string>? LogSink { get; set; }

	public static T Load<T>(string path) where T : new() {
		try {
			return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? new T();
		}
		catch (FileNotFoundException) {
			return new T();
		}
		catch (DirectoryNotFoundException) {
			return new T();
		}
		catch (Exception exception) {
			Log($"could not read {path}: {exception.Message}; using defaults");
			return new T();
		}
	}

	public static void Save<T>(string path, T value) {
		Directory.CreateDirectory(AppDir);
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
		File.Move(temporary, path, overwrite: true);
	}

	public static ExplorerConfig LoadConfig() {
		return Load<ExplorerConfig>(ConfigFile);
	}

	public static void Log(string message) {
		try {
			LogSink?.Invoke($"[explorer] {message}");
		}
		catch {
			// Logging must never crash the service.
		}
	}
}
