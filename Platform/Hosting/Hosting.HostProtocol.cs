using System.Text.Json;
using System.Text.Json.Serialization;

using Alexander.Graphics;

namespace Alexander.Hosting;

// GUI -> Shell                                 Shell -> GUI
//   init        metrics, theme                   progress    stage?, text, isError  (build/load/rebuild)
//   input       id, text                         output      text, isError          (command output)
//   complete    id, text                         echo        text                   (command a view ran)
//   openView    viewInstance, viewId, w, h       result      id, action, text       (input finished)
//   resizeView  viewInstance, w, h               completions id, items
//   viewInput   viewInstance, text=kind, ...     state       state                  (prompt/folder/layer/views)
//   closeView   viewInstance                     frame       viewInstance, ops, text=title
//   shutdown                                     viewClosed  viewInstance, text=reason
//                                                error       id, text               (request failed)
//
// The Shell owns everything about Core - building, loading, /rebuild -
// and only reports progress. Requests are answered asynchronously; the
// GUI never waits on the Shell from its UI thread.
public static class HostMessageTypes {
	public const string Init = "init";
	public const string Input = "input";
	public const string Complete = "complete";
	public const string OpenView = "openView";
	public const string ResizeView = "resizeView";
	public const string ViewInput = "viewInput";
	public const string CloseView = "closeView";
	public const string Shutdown = "shutdown";

	public const string Progress = "progress";
	public const string Output = "output";
	public const string Echo = "echo";
	public const string Result = "result";
	public const string Completions = "completions";
	public const string State = "state";
	public const string Frame = "frame";
	public const string ViewClosed = "viewClosed";
	public const string Error = "error";
}

// progress.stage values. Ready and Failed end a startup or rebuild.
public static class HostStages {
	public const string Building = "building";
	public const string Loading = "loading";
	public const string Ready = "ready";
	public const string Failed = "failed";
}

// viewInput kinds (carried in Text).
public static class ViewInputKinds {
	public const string Move = "move";
	public const string Down = "down";
	public const string Up = "up";
	public const string Leave = "leave";
	public const string Wheel = "wheel";
	public const string Key = "key";
}

// One envelope for every message; each type uses the fields it needs.
public sealed class HostMessage {
	public string Type { get; set; } = "";

	// Correlates a request with its reply; 0 for unsolicited messages.
	public long Id { get; set; }

	public string? Text { get; set; }
	public bool IsError { get; set; }
	public string? Stage { get; set; }

	// result: "continue", "exit" or "rebuild" (DtAction, lower-case).
	public string? Action { get; set; }

	public SessionState? State { get; set; }
	public List<string>? Items { get; set; }

	public string? ViewId { get; set; }
	public long ViewInstance { get; set; }
	public double Width { get; set; }
	public double Height { get; set; }
	public double X { get; set; }
	public double Y { get; set; }
	public double Delta { get; set; }
	public string? Button { get; set; }
	public string? Key { get; set; }
	public int Modifiers { get; set; }
	public List<DrawOp>? Ops { get; set; }

	public TextMetrics? Metrics { get; set; }
	public string? Theme { get; set; }
	public int Version { get; set; }
}

public sealed class SessionState {
	public string Prompt { get; set; } = "";
	public string Folder { get; set; } = "";
	public string? Layer { get; set; }
	public List<ViewDescriptor> Views { get; set; } = [];
}

public sealed record ViewDescriptor(string Id, string Title, string Description);

public static class HostProtocol {
	public const int Version = 1;

	// The Shell's command-line switch for host mode.
	public const string HostArgument = "--host";

	public static readonly JsonSerializerOptions Json = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
	};

	public static string Serialize(HostMessage message) {
		return JsonSerializer.Serialize(message, Json);
	}

	// Null for anything that isn't a protocol line (e.g. "dotnet run"
	// build chatter before the Shell takes over stdout).
	public static HostMessage? TryParse(string line) {
		if (!line.StartsWith('{')) {
			return null;
		}

		try {
			return JsonSerializer.Deserialize<HostMessage>(line, Json);
		}
		catch (JsonException) {
			return null;
		}
	}
}
