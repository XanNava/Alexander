namespace Alexander.Core;

// Which service command layer currently has control; null means the
// shell layer. Set by "/<layer>" (ServiceLayerHandler), cleared by /exit
// (LifecycleCommandHandlers), and read by CommandLayer for routing, the
// prompt label and completion. Not persisted - a relaunch or /rebuild
// starts back at the shell layer.
public sealed class ActiveLayerState {
	public IServiceCommandLayer? Current { get; set; }
}
