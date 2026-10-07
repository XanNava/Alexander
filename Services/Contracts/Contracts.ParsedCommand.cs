using System.Text;

namespace Alexander.Services.Contracts;

// Raised for "you gave this command bad input" - things the interactive
// user can fix by typing something different: a missing flag, a
// malformed value. Caught first and printed as-is by CommandLayer, ahead
// of the broader catch that maps real faults (IOException,
// UnauthorizedAccessException, ...) to a generic message.
public sealed class CommandValidationException(string message) : Exception(message);

public sealed record ParsedCommand(
	string Text,
	IReadOnlyDictionary<string, string> Flags,
	IReadOnlySet<string> BareFlags) {

	public static ParsedCommand From(string input) {
		var tokens = Tokenize(input);
		var commandParts = new List<string>();
		var flags = new Dictionary<string, string>(
			StringComparer.OrdinalIgnoreCase);
		var bareFlags = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);

		var index = 0;

		while (index < tokens.Count
			&& !tokens[index].StartsWith("--", StringComparison.Ordinal)) {

			commandParts.Add(tokens[index++]);
		}

		while (index < tokens.Count) {
			var token = tokens[index++];

			if (!token.StartsWith("--", StringComparison.Ordinal)) {
				throw new CommandValidationException(
					"Unexpected command argument. Quote values containing spaces.");
			}

			var equalsIndex = token.IndexOf('=');
			string name;
			string value;

			if (equalsIndex >= 0) {
				name = token[..equalsIndex];
				value = token[(equalsIndex + 1)..];
			} else {
				name = token;

				// A flag with nothing after it (or followed by another
				// flag) is written bare, e.g. plain --confirm. Whether
				// that's actually allowed depends on the matched
				// command's own flag definitions (see CommandDispatcher),
				// which this parser has no knowledge of - it just
				// records the fact.
				if (index >= tokens.Count
					|| tokens[index].StartsWith("--", StringComparison.Ordinal)) {

					value = "true";
					bareFlags.Add(name);
				} else {
					value = tokens[index++];
				}
			}

			if (name.Length <= 2 || !flags.TryAdd(name, value)) {
				throw new CommandValidationException(
					"Invalid or duplicate command flag.");
			}
		}

		return new ParsedCommand(
			string.Join(' ', commandParts),
			flags,
			bareFlags);
	}

	public string Required(string name) {
		if (!Flags.TryGetValue(name, out var value)
			|| string.IsNullOrWhiteSpace(value)) {

			throw new CommandValidationException(
				$"A value for {name} is required.");
		}

		return value;
	}

	private static List<string> Tokenize(string input) {
		var tokens = new List<string>();
		var current = new StringBuilder();
		var quote = '\0';
		var hasToken = false;

		for (var index = 0; index < input.Length; index++) {
			var character = input[index];

			if (quote != '\0') {
				if (character == quote) {
					quote = '\0';
				} else if (character == '\\'
					  && index + 1 < input.Length
					  && input[index + 1] == quote) {

					current.Append(input[++index]);
				} else {
					current.Append(character);
				}

				continue;
			}

			if (character is '"' or '\'') {
				quote = character;
				hasToken = true;
			} else if (char.IsWhiteSpace(character)) {
				if (hasToken) {
					tokens.Add(current.ToString());
					current.Clear();
					hasToken = false;
				}
			} else {
				current.Append(character);
				hasToken = true;
			}
		}

		if (quote != '\0') {
			throw new CommandValidationException(
				"An argument has an unclosed quote.");
		}

		if (hasToken) {
			tokens.Add(current.ToString());
		}

		return tokens;
	}
}
