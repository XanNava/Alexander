namespace Alexander.Contracts;

// Read-only session details a host can show and persist. Implemented by
// the IDT that CoreEntryPoint returns; a host checks for it with "is".
public interface ISessionInfo {
	string CurrentFolder { get; }

	// Name of the service layer that has control, or null at the shell layer.
	string? ActiveLayer { get; }
}
