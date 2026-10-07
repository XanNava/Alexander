namespace Alexander.Core;

// The one piece of session-like state this app has: which folder /ls,
// /cat, /make, /run, /exec (and friends) currently apply to. Persisted to
// AppState.CurrentFolder (see CommandLayer.StartAsync/FileCommandHandlers)
// so it survives a relaunch or a /rebuild without needing anything
// resembling the old app's session-resume mechanism.
//
// Service layers see it read-only through ICurrentFolder.
public sealed class CurrentFolderState : ICurrentFolder {
	public string Folder { get; set; } = "";
}
