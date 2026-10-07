namespace Alexander.Services.Contracts;

// Read-only view of the shell's current folder, so a service layer can
// resolve relative paths the same way the built-in file commands do
// without depending on Core. Core's CurrentFolderState implements it.
public interface ICurrentFolder {
	string Folder { get; }

	// Relative to Folder unless already absolute.
	string Resolve(string path) {
		return Path.GetFullPath(
			Path.IsPathRooted(path) ? path : Path.Combine(Folder, path));
	}
}
