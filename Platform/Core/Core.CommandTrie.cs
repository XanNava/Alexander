namespace Alexander.Core;

// A standard prefix tree (trie), built once from the catalog's command
// texts. ConsoleLineReader (in Shell) calls this on every keystroke while
// typing a command name, via ICoreInteraction.GetCompletions - the point
// is that this doesn't get slower as the catalog grows.
internal sealed class CommandTrie {
	private sealed class Node {
		public Dictionary<char, Node> Children { get; } = new();
		public string? CommandText { get; set; }
	}

	private readonly Node root = new();

	public CommandTrie(IEnumerable<string> commandTexts) {
		foreach (var text in commandTexts) {
			Insert(text);
		}
	}

	// Every full command text whose lowercase form starts with prefix
	// (case-insensitive), sorted. Empty if nothing matches.
	public IReadOnlyList<string> FindByPrefix(string prefix) {
		var node = root;

		foreach (var character in prefix.ToLowerInvariant()) {
			if (!node.Children.TryGetValue(character, out var next)) {
				return [];
			}

			node = next;
		}

		var results = new List<string>();
		Collect(node, results);
		results.Sort(StringComparer.OrdinalIgnoreCase);
		return results;
	}

	private void Insert(string text) {
		var node = root;

		foreach (var character in text.ToLowerInvariant()) {
			if (!node.Children.TryGetValue(character, out var next)) {
				next = new Node();
				node.Children[character] = next;
			}

			node = next;
		}

		node.CommandText = text;
	}

	private static void Collect(Node node, List<string> results) {
		if (node.CommandText is not null) {
			results.Add(node.CommandText);
		}

		foreach (var child in node.Children.Values) {
			Collect(child, results);
		}
	}
}
