using Microsoft.Extensions.DependencyInjection;

using Alexander.Shared;

namespace Alexander.Core;

// Registration grouped by concern, the same convention ASP.NET Core apps
// use (AddControllers(), AddSwaggerGen(), ...) - keeps CoreEntryPoint.Create
// itself readable as the app grows. Service modules own their own
// registration (AddAlexanderFolderServices in Alexander.Services.Automation,
// AddAlexanderAi in Alexander.Services.AI).
internal static class ServiceCollectionExtensions {
	public static IServiceCollection AddAlexanderShared(
		this IServiceCollection services,
		AppPaths paths) {

		services.AddSingleton(paths);
		services.AddSingleton<IStateStore, StateStore>();
		services.AddSingleton<IOutputWriter, ConsoleOutputWriter>();
		services.AddSingleton<IAppLog, FileAppLog>();

		return services;
	}

	public static IServiceCollection AddAlexanderCore(
		this IServiceCollection services) {

		services.AddSingleton<CommandCatalog>();
		services.AddSingleton<CurrentFolderState>();

		// Service layers get the same instance, read-only, through the contract.
		services.AddSingleton<ICurrentFolder>(provider =>
			provider.GetRequiredService<CurrentFolderState>());

		services.AddSingleton<ActiveLayerState>();
		services.AddSingleton<CommandDispatcher>();
		services.AddSingleton<CommandLayer>();
		services.AddSingleton<IVersionStore, VersionStore>();

		return services;
	}

	// The shell layer's own command groups. Service command layers are
	// not registered here: each service registers its IServiceCommandLayer
	// itself, and CommandCatalog/CommandDispatcher pick them all up.
	public static IServiceCollection AddAlexanderCommandHandlers(
		this IServiceCollection services) {

		services.AddSingleton<ICommandHandler, CoreCommandHandlers>();
		services.AddSingleton<ICommandHandler, LifecycleCommandHandlers>();
		services.AddSingleton<ICommandHandler, FileCommandHandlers>();

		return services;
	}
}
