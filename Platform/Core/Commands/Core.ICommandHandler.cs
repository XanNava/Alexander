using Alexander.Contracts;

namespace Alexander.Core;

// One handler serves a related group of commands (e.g. every /service *
// command). CommandDispatcher indexes every registered handler by the
// command texts it claims (HandledCommands) and routes each parsed
// command to the right one - adding a new command group means adding a
// new handler class and registering it in ServiceCollectionExtensions;
// CommandDispatcher itself never changes.
public interface ICommandHandler {
	IReadOnlyList<string> HandledCommands { get; }

	Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken);
}
