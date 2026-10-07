using System.Xml.Linq;

using Alexander.Shared;

namespace Alexander.Core;

public sealed record AppVersion(string? Number, string? Notes, string Path, bool Found);

public interface IVersionStore {
	Task<AppVersion> LoadAsync(CancellationToken cancellationToken);
}

// version.xml looks like:
//   <Version>
//     <Number>0.1.0</Number>
//     <Notes>What changed in this build.</Notes>
//   </Version>
public sealed class VersionStore(AppPaths paths) : IVersionStore {
	public async Task<AppVersion> LoadAsync(CancellationToken cancellationToken) {
		var path = paths.ResolveVersionFile();

		if (!File.Exists(path)) {
			return new AppVersion(null, null, path, Found: false);
		}

		try {
			var document = await Task.Run(() => XDocument.Load(path), cancellationToken);

			return new AppVersion(
				document.Root?.Element("Number")?.Value,
				document.Root?.Element("Notes")?.Value,
				path,
				Found: true);
		}
		catch (Exception exception) when (
			exception is IOException
				or UnauthorizedAccessException
				or System.Xml.XmlException) {

			return new AppVersion(null, null, path, Found: false);
		}
	}
}
