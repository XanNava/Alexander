using System.Reflection;
using System.Runtime.CompilerServices;

using Alexander.Contracts;

namespace Alexander.Shell;

internal sealed class CoreHost : IAsyncDisposable {
	private PluginLoadContext? context;
	private IDT? current;

	public IDT Current =>
		current ?? throw new InvalidOperationException("Core is not loaded.");

	// Null while Core hasn't been loaded (yet), or after disposal.
	public IDT? CurrentOrNull => current;

	// Set in host mode (HostSession): every Core generation loaded from
	// then on writes its output here - i.e. to the GUI - instead of the
	// console. Your existing build/rebuild code doesn't need to change.
	public IOutputSink? Output { get; set; }

	public async Task LoadAsync(
		string coreAssemblyPath,
		string launchFolder,
		string shellBaseDirectory,
		CancellationToken cancellationToken) {

		var nextContext = new PluginLoadContext(coreAssemblyPath);

		Assembly assembly;

		await using (var stream = File.OpenRead(coreAssemblyPath)) {
			assembly = nextContext.LoadFromStream(stream);
		}

		var entryPointType = assembly.GetType("Alexander.Core.CoreEntryPoint")
			?? throw new InvalidOperationException(
				"Alexander.Core.CoreEntryPoint was not found in the " +
				"built Core assembly.");

		IDT instance;

		if (Output is { } output) {
			var createWithOutput = entryPointType.GetMethod(
				"CreateWithOutput",
				BindingFlags.Public | BindingFlags.Static,
				[typeof(string), typeof(string), typeof(IOutputSink)])
				?? throw new InvalidOperationException(
					"CoreEntryPoint.CreateWithOutput(string, string, IOutputSink) was not found.");

			instance = (IDT)createWithOutput.Invoke(
				null,
				[launchFolder, shellBaseDirectory, output])!;
		} else {
			var createMethod = entryPointType.GetMethod(
				"Create",
				BindingFlags.Public | BindingFlags.Static,
				[typeof(string), typeof(string)])
				?? throw new InvalidOperationException(
					"CoreEntryPoint.Create(string, string) was not found.");

			instance = (IDT)createMethod.Invoke(
				null,
				[launchFolder, shellBaseDirectory])!;
		}

		context = nextContext;
		current = instance;

		cancellationToken.ThrowIfCancellationRequested();
	}

	public async Task SwapAsync(
		string coreAssemblyPath,
		string launchFolder,
		string shellBaseDirectory,
		CancellationToken cancellationToken) {

		var previous = (Context: context, Instance: current);

		await LoadAsync(
			coreAssemblyPath,
			launchFolder,
			shellBaseDirectory,
			cancellationToken);

		if (previous.Instance is not null) {
			await previous.Instance.DisposeAsync();
		}

		if (previous.Context is not null) {
			await UnloadContextAsync(previous.Context);
		}
	}

	public async ValueTask DisposeAsync() {
		if (current is not null) {
			await current.DisposeAsync();
			current = null;
		}

		if (context is not null) {
			await UnloadContextAsync(context);
			context = null;
		}
	}

	private static async Task UnloadContextAsync(PluginLoadContext context) {
		var weakReference = CreateWeakReference(context);

		for (var attempt = 0; weakReference.IsAlive && attempt < 10; attempt++) {
			GC.Collect();
			GC.WaitForPendingFinalizers();
			await Task.Delay(50);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference CreateWeakReference(PluginLoadContext context) {
		context.Unload();

		return new WeakReference(context);
	}
}
