namespace Alexander.Services.Contracts;

// What /help prints, what flag validation checks against, and what
// tab-completion suggests for one command.
//
// A service command layer declares its commands relative to itself
// ("ask", not "/ai ask") and leaves Section at its default; the shell
// command layer adds the "/ai" prefix and sets Section to the layer's
// name when it builds the catalog.
public sealed record CommandFlag(
	string Text,
	string Description,
	bool IsBoolean = false);

public sealed record CommandDefinition(
	string Text,
	string Description,
	IReadOnlyList<CommandFlag> Flags,
	string Section = "command");
