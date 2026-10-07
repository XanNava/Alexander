using System.Reflection;
using System.Runtime.Loader;

namespace Alexander.Shell;

// One instance per Core "generation." isCollectible:true is what makes
// Unload() possible at all - without it this would behave like the
// default, permanent load context.
internal sealed class PluginLoadContext : AssemblyLoadContext {
	private readonly AssemblyDependencyResolver resolver;

	public PluginLoadContext(string coreAssemblyPath)
		: base(name: $"Core-{Guid.NewGuid():N}", isCollectible: true) {

		// Reads Alexander.Platform.Core.deps.json beside the dll, so this
		// context can locate Core's own package dependencies
		// (Microsoft.Extensions.*, etc.) without Shell knowing about them.
		resolver = new AssemblyDependencyResolver(coreAssemblyPath);
	}

	protected override Assembly? Load(AssemblyName assemblyName) {
		// Contracts must resolve to the SAME assembly instance the Shell
		// already loaded into the default context - otherwise IDT here
		// and IDT there are different types and every cast fails.
		// Returning null falls back to the default context, which is
		// exactly what we want for Contracts (and anything else already
		// loaded there, e.g. framework assemblies).
		if (string.Equals(
			assemblyName.Name,
			"Alexander.Contracts",
			StringComparison.Ordinal)) {

			return null;
		}

		var path = resolver.ResolveAssemblyToPath(assemblyName);

		return path is null ? null : LoadFromAssemblyPath(path);
	}

	protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) {
		var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);

		return path is null
			? IntPtr.Zero
			: LoadUnmanagedDllFromPath(path);
	}
}
