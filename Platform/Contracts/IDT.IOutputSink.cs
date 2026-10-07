namespace Alexander.Contracts;

// Where a Core instance sends its output when the host isn't a console.
// Alexander.Gui passes one to CoreEntryPoint.CreateWithOutput; it may be
// called from any thread (background services write to it too).
public interface IOutputSink {
	void WriteLine(string text, bool isError);
}
