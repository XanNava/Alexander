using Alexander.Services.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace Alexander.Services.Explorer;

// Everything the Explorer service needs, registered by the service itself
// so Core only has to call AddAlexanderExplorer().
public static class ExplorerServiceCollectionExtensions {
	public static IServiceCollection AddAlexanderExplorer(
		this IServiceCollection services) {

		// Explorer windows, Shell COM and user32 only exist on Windows.
		// Elsewhere the layer simply isn't registered, so /explorer never
		// appears - and this check is also what satisfies the CA1416
		// platform analyzer for the Windows-only types below.
		if (!OperatingSystem.IsWindows()) {
			return services;
		}

		// Singleton so it lives as long as this Core generation, and is
		// disposed (stopping its threads) when the ServiceProvider is.
		services.AddSingleton<ExplorerHost>();

		// Registering the layer is the whole hookup: the shell command
		// layer discovers every IServiceCommandLayer and wires in
		// /explorer and its commands from it.
		services.AddSingleton<IServiceCommandLayer, ExplorerCommandLayer>();

		return services;
	}
}
