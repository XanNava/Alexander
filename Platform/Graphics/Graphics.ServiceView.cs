namespace Alexander.Graphics;

[Flags]
public enum KeyModifiers {
	None = 0,
	Shift = 1,
	Control = 2,
	Alt = 4
}

public enum PointerButton {
	None,
	Left,
	Right,
	Middle
}

public sealed record PointerEvent(Point Position, PointerButton Button, KeyModifiers Modifiers);

public sealed record WheelEvent(Point Position, double Delta, KeyModifiers Modifiers);

// Key is the host's key name (e.g. "Enter", "Up", "A").
public sealed record KeyEvent(string Key, KeyModifiers Modifiers);

// What a view can ask of the window hosting it.
public interface IViewHost {
	// The owning session's current folder.
	string CurrentFolder { get; }

	// Runs a command in the owning session exactly as if typed into its
	// command window (it's echoed there, and its output appears there).
	Task RunCommandAsync(string input);
}

public sealed record ServiceViewInfo(string Id, string Title, string Description);

// Registered in DI by a service (as IServiceViewProvider) to offer its
// custom windows. Ids should be namespaced by service, e.g.
// "service.status", so they stay unique across services.
public interface IServiceViewProvider {
	IReadOnlyList<ServiceViewInfo> Views { get; }

	ServiceView CreateView(string id, IViewHost host);
}

// A service's custom window. Retained by the GUI, drawn on demand:
//   Render       - draw the whole view; called whenever it needs repainting.
//   Invalidate() - ask for a repaint (safe from any thread).
//   RefreshInterval - optional periodic repaint, for live status views.
//   On...        - input, delivered on the GUI thread.
//
// Render runs on the GUI thread, so read shared service state the same
// thread-safe way any other caller would.
public abstract class ServiceView(IViewHost host) : IDisposable {
	protected IViewHost Host { get; } = host;

	public abstract string Title { get; }

	public virtual TimeSpan? RefreshInterval => null;

	public event EventHandler? Invalidated;

	public abstract void Render(IGraphics graphics);

	public virtual void OnPointerMove(PointerEvent pointer) { }
	public virtual void OnPointerDown(PointerEvent pointer) { }
	public virtual void OnPointerUp(PointerEvent pointer) { }
	public virtual void OnPointerLeave() { }
	public virtual void OnWheel(WheelEvent wheel) { }
	public virtual void OnKeyDown(KeyEvent key) { }

	public virtual void Dispose() { }

	protected void Invalidate() {
		Invalidated?.Invoke(this, EventArgs.Empty);
	}
}
