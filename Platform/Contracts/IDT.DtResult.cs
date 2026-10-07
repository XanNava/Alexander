namespace Alexander.Contracts;

// What Core tells the shell to do after handling one line of input.
public enum DtAction {
	Continue,
	Exit,
	Rebuild
}

public sealed record DtResult(DtAction Action, string? Message = null) {
	public static DtResult Continue(string? message = null) {
		return new DtResult(DtAction.Continue, message);
	}

	public static DtResult Exit(string? message = null) {
		return new DtResult(DtAction.Exit, message);
	}

	public static DtResult Rebuild(string? message = null) {
		return new DtResult(DtAction.Rebuild, message);
	}
}
