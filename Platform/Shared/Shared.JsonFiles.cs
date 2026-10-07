using System.Text.Json;

namespace Alexander.Shared;

public static class JsonFiles {
	public static JsonSerializerOptions Options { get; } =
		new(JsonSerializerDefaults.Web) {
			WriteIndented = true
		};

	public static async Task<T?> ReadAsync<T>(
		string path,
		CancellationToken cancellationToken) {

		if (!File.Exists(path)) {
			return default;
		}

		await using var stream = File.OpenRead(path);

		return await JsonSerializer.DeserializeAsync<T>(
			stream,
			Options,
			cancellationToken);
	}

	public static async Task WriteAsync<T>(
		string path,
		T value,
		CancellationToken cancellationToken) {

		var directory = Path.GetDirectoryName(path);

		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";

		try {
			await using (var stream = new FileStream(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 4096,
				useAsync: true)) {

				await JsonSerializer.SerializeAsync(
					stream,
					value,
					Options,
					cancellationToken);

				await stream.FlushAsync(cancellationToken);
			}

			cancellationToken.ThrowIfCancellationRequested();

			// Replace only after the complete JSON has been written.
			File.Move(temporaryPath, path, overwrite: true);
		}
		finally {
			if (File.Exists(temporaryPath)) {
				File.Delete(temporaryPath);
			}
		}
	}

	public static string SafeFileName(string value) {
		var invalid = Path.GetInvalidFileNameChars();

		var safe = new string(value.Select(character =>
			invalid.Contains(character)
				|| character is '/' or '\\' or ':'
				|| char.IsControl(character)
					? '-'
					: character).ToArray());

		return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
	}
}
