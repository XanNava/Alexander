using Alexander.Contracts;
using Alexander.Shared;

namespace Alexander.Core;

// IOutputWriter for hosts that aren't a console (Alexander.Gui). Registered
// by CoreEntryPoint.CreateWithOutput in place of ConsoleOutputWriter, so
// every command handler and background service writes to the host's sink
// without knowing it.
//
// These three members are the ones Core calls; if IOutputWriter declares
// any others, add them here the same way.
internal sealed class SinkOutputWriter(IOutputSink sink) : IOutputWriter {
	public void WriteLine() {
		sink.WriteLine("", isError: false);
	}

	public void WriteLine(string text) {
		sink.WriteLine(text, isError: false);
	}

	public void WriteError(string text) {
		sink.WriteLine(text, isError: true);
	}
}
