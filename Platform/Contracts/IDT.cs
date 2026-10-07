namespace Alexander.Contracts;

// The reloadable command layer. The shell only ever talks to Core through
// this interface, never through a concrete Core type - Core types live
// inside a collectible AssemblyLoadContext that gets thrown away on every
// /rebuild, and only types from THIS assembly are safe to hold across
// that boundary.
//
// Whatever lives in Contracts is loaded once into the default
// AssemblyLoadContext and is NOT part of what /rebuild reloads - changing
// this interface requires rebuilding and restarting the Shell, unlike an
// ordinary Core-only change.
public interface IDT : ICoreLifecycle, ICoreInteraction {
}
