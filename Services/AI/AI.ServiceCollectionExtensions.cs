using Alexander.Services.Contracts;

using Microsoft.Extensions.DependencyInjection;

namespace Alexander.Services.AI;

// Everything the AI service needs, registered by the service itself so
// Core only has to call AddAlexanderAi().
public static class AiServiceCollectionExtensions {
	public static IServiceCollection AddAlexanderAi(
		this IServiceCollection services) {

		services
			.AddHttpClient("Ai", client => {
				client.Timeout = TimeSpan.FromMinutes(3);
			})
			.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler {
				AllowAutoRedirect = false
			});

		services.AddSingleton<IAiProvider, OpenAiResponsesProvider>();
		services.AddSingleton<IAiProvider, OpenAiCompatibleProvider>();
		services.AddSingleton<IAiProvider, ClaudeProvider>();
		services.AddSingleton<IAiClient, AiClient>();
		services.AddSingleton<IAiClientStore, AiClientStore>();

		// Registering the layer is the whole hookup: the shell command
		// layer discovers every IServiceCommandLayer and wires in /ai and
		// its commands from it.
		services.AddSingleton<IServiceCommandLayer, AiCommandLayer>();

		return services;
	}
}
