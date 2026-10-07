using Alexander.Contracts;

namespace Alexander.Core;

// One handler serves a related group of the shell layer's own commands
// (e.g. every file command). CommandDispatcher indexes every registered
// handler by the command texts it claims (HandledCommands) and routes
// each parsed command to the right one - adding a new shell-level
// command group means adding a handler class and registering it in
// ServiceCollectionExtensions.AddAlexanderCommandHandlers.
//
// Service-owned commands (/ai, /service, ...) don't use this interface:
// a service implements IServiceCommandLayer instead, and the dispatcher
// wraps each layer in a ServiceLayerHandler. Either way,
// CommandDispatcher itself never changes.
public interface ICommandHandler {
	IReadOnlyList<string> HandledCommands { get; }

	Task<DtResult> ExecuteAsync(
		ParsedCommand command,
		CancellationToken cancellationToken);
}
