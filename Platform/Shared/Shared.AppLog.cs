namespace Alexander.Shared;

// A background service (a repeater, a watcher) runs unattended - if
// nobody's looking at the terminal when it fires, its output would
// otherwise just vanish. Every command and every service writes through
// this so there's always a persistent record of what ran and what it did,
// independent of whether IOutputWriter's console output was visible at
// the time.
public interface IAppLog {
	void WriteLine(string message);
}

public sealed class FileAppLog(AppPaths paths) : IAppLog {
	private readonly object writeLock = new();

	public void WriteLine(string message) {
		lock (writeLock) {
			try {
				File.AppendAllText(
					paths.LogsFile,
					$"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
			}
			catch (IOException) {
				// Logging is best-effort - a locked or momentarily
				// inaccessible log file should never take down whatever
				// was actually running.
			}
		}
	}
}
