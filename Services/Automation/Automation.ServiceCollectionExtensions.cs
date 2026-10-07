using Alexander.Graphics;
using Alexander.Services.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace Alexander.Services.Automation;

// Everything the automation service needs, registered by the service
// itself so Core only has to call AddAlexanderFolderServices().
public static class AutomationServiceCollectionExtensions {
	// Folder-worker service TYPES (repeat, watch, ...) and the registry
	// that constructs fresh instances of them on demand. Each type is
	// Transient (not Singleton) so the same type can back more than one
	// running instance at once. Registering a new type is one
	// AddTransient line plus one factory-map entry - nothing about the
	// command layer or Core needs to change for it.
	public static IServiceCollection AddAlexanderFolderServices(
		this IServiceCollection services) {

		services.AddTransient<RepeatingCommandService>();
		services.AddTransient<FileWatcherService>();

		services.AddSingleton<IServiceRegistry>(provider => new ServiceRegistry(
			new Dictionary<string, Func<IFolderService>> {
				["repeat"] = () => provider.GetRequiredService<RepeatingCommandService>(),
				["watch"] = () => provider.GetRequiredService<FileWatcherService>()
			}));

		// Registering the layer is the whole hookup: the shell command
		// layer discovers every IServiceCommandLayer and wires in
		// /service and its commands from it.
		services.AddSingleton<IServiceCommandLayer, AutomationCommandLayer>();

		// Custom windows for the GUI (see AutomationStatusView). Hosts
		// without a GUI never ask for them.
		services.AddSingleton<IServiceViewProvider, AutomationViewProvider>();

		return services;
	}
}
