using Alexander.Contracts;

namespace Alexander.Services.Contracts;

// A service's own command layer. Registering one in DI as
// IServiceCommandLayer is all it takes to hook it into the shell
// command layer (Core's CommandLayer), which picks up every registered
// layer at startup:
//
//   From the shell layer, each command is reachable with the layer's
//   prefix, e.g. "/ai ask --prompt=...".
//
//   Typing the bare prefix ("/ai") transfers control to the layer.
//   Un-slashed input then runs as this layer's commands ("ask
//   --prompt=..."), slashed input still reaches the shell layer ("/ls",
//   "/help"), and /exit hands control back.
//
// The layer never sees its own prefix: ExecuteAsync receives
// command.Text exactly as declared in Commands ("ask"), with its flags
// already validated against that definition.
public interface IServiceCommandLayer {
	// Prefix and /help section name, e.g. "ai". No spaces or slashes.
	string Name { get; }

	// One line for /help saying what the layer is for.
	string Description { get; }

	IReadOnlyList<CommandDefinition> Commands { get; }

	Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken);

	// Called only while this layer has control, for input that isn't one
	// of its commands. Return null (the default) to report it as an
	// unknown command.
	Task<DtResult?> HandleTextAsync(
		string text,
		CancellationToken cancellationToken) {

		return Task.FromResult<DtResult?>(null);
	}
}
