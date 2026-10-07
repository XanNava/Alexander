using Alexander.Contracts;
using Alexander.Graphics;
using Alexander.Services.AI;
using Alexander.Services.Automation;
using Alexander.Services.Explorer;
using Alexander.Shared;

using Microsoft.Extensions.DependencyInjection;

namespace Alexander.Core;

// The one method Shell finds by reflection (see
// Platform/Shell/Alexander.Platform.Shell.CoreHost.cs) to
// build a new Core generation. launchFolder is Environment.CurrentDirectory
// as seen by Shell; shellBaseDirectory is Shell's own exe folder (used
// only for locating version.xml - see AppPaths.ResolveVersionFile).
public static class CoreEntryPoint {
	public static IDT Create(string launchFolder, string shellBaseDirectory) {
		return Build(launchFolder, shellBaseDirectory, output: null);
	}

	// For hosts that aren't a console (Alexander.Gui): output goes to the
	// sink instead. A separate name rather than an overload, because the
	// Shell looks Create up by name and an overload would make that
	// lookup ambiguous.
	public static IDT CreateWithOutput(
		string launchFolder,
		string shellBaseDirectory,
		IOutputSink output) {

		return Build(launchFolder, shellBaseDirectory, output);
	}

	private static IDT Build(
		string launchFolder,
		string shellBaseDirectory,
		IOutputSink? output) {

		var services = new ServiceCollection();
		var paths = new AppPaths(launchFolder, shellBaseDirectory);

		services
			.AddAlexanderShared(paths)
			.AddAlexanderCore()
			.AddAlexanderFolderServices()
			.AddAlexanderAi()
			.AddAlexanderExplorer()
			.AddAlexanderCommandHandlers();

		// Registered last, so it's what IOutputWriter resolves to.
		if (output is not null) {
			services.AddSingleton<IOutputWriter>(new SinkOutputWriter(output));
		}

		var provider = services.BuildServiceProvider();

		return new CoreInstance(provider);
	}

	// Also exposes ISessionInfo and IServiceViewProvider, which a GUI host
	// discovers with "is" - the console Shell never needs to know.
	private sealed class CoreInstance(ServiceProvider provider)
		: IDT, ISessionInfo, IServiceViewProvider {

		private readonly CommandLayer commandLayer =
			provider.GetRequiredService<CommandLayer>();

		private readonly CurrentFolderState folderState =
			provider.GetRequiredService<CurrentFolderState>();

		private readonly ActiveLayerState activeLayer =
			provider.GetRequiredService<ActiveLayerState>();

		private readonly IServiceViewProvider[] viewProviders =
			provider.GetServices<IServiceViewProvider>().ToArray();

		public Task<string?> StartAsync(CancellationToken cancellationToken) {
			return commandLayer.StartAsync(cancellationToken);
		}

		public string PromptLabel => commandLayer.PromptLabel;

		public IReadOnlyList<string> GetCompletions(string input) {
			return commandLayer.GetCompletions(input);
		}

		public Task<DtResult> HandleInputAsync(
			string input,
			CancellationToken cancellationToken) {

			return commandLayer.HandleInputAsync(input, cancellationToken);
		}

		public string CurrentFolder => folderState.Folder;

		public string? ActiveLayer => activeLayer.Current?.Name;

		public IReadOnlyList<ServiceViewInfo> Views =>
			[.. viewProviders.SelectMany(viewProvider => viewProvider.Views)];

		public ServiceView CreateView(string id, IViewHost host) {
			var owner = viewProviders.FirstOrDefault(viewProvider =>
				viewProvider.Views.Any(view =>
					string.Equals(view.Id, id, StringComparison.OrdinalIgnoreCase)))
				?? throw new ArgumentException($"No service view named '{id}'.", nameof(id));

			return owner.CreateView(id, host);
		}

		// Disposing the ServiceProvider releases the HttpClientFactory's
		// handlers and anything else IDisposable that DI constructed
		// (e.g. ExplorerHost's threads), after commandLayer.DisposeAsync
		// has stopped any running folder-worker services cleanly.
		public async ValueTask DisposeAsync() {
			await commandLayer.DisposeAsync();
			await provider.DisposeAsync();
		}
	}
}
