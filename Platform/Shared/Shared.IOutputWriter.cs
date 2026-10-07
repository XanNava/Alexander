namespace Alexander.Shared;

// Decouples command logic from the console: every handler prints through
// this instead of calling Console.* directly, so output is one
// consistent path regardless of what triggered it (a typed command, a
// background service reporting what it did, ...).
public interface IOutputWriter {
	void WriteLine(string text = "");
	void Write(string text);
	void WriteColored(string text, ConsoleColor color);
	void WriteError(string text);
}

// The whole app is single-threaded for the console today, but a folder
// service (a repeater, a watcher) can legitimately want to print from a
// background Task while the main loop is also mid-render - the lock
// costs nothing while nothing contends for it, and means a background
// service's output can never interleave mid-line with the prompt's own
// colored text.
public sealed class ConsoleOutputWriter : IOutputWriter {
	private readonly object writeLock = new();

	public void WriteLine(string text = "") {
		lock (writeLock) {
			Console.WriteLine(text);
		}
	}

	public void Write(string text) {
		lock (writeLock) {
			Console.Write(text);
		}
	}

	public void WriteColored(string text, ConsoleColor color) {
		lock (writeLock) {
			Console.ForegroundColor = color;
			Console.Write(text);
			Console.ResetColor();
		}
	}

	public void WriteError(string text) {
		lock (writeLock) {
			Console.Error.WriteLine(text);
		}
	}
}
