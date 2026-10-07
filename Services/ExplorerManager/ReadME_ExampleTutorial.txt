The tutorial for any new service
Create a folder in Alexander.Service with its own namespace (Alexander.Services.Explorer).
Bring over the service's working code: the logic that actually does things.
Write a class implementing IServiceCommandLayer. Its Name becomes the prefix (/explorer), and its Commands list replaces whatever CLI or UI the program had.
Write an AddAlexanderX() extension that registers the layer as IServiceCommandLayer.
Call it from CoreEntryPoint.Create.

Nothing else in Core changes. The catalog, dispatcher, /help, tab completion and /explorer layer-entering all come from the registration.

How ExplorerManager mapped over

Kept, with type renames: ExplorerShell, Manager (now ExplorerManager), Config (now ExplorerConfig), PathKey and the native interop. The decision logic is unchanged. Everything is internal, so none of it leaks into Core.

Program.cs became ExplorerCommandLayer. The CLI commands are now layer commands: open, list, groups, group add, group remove, default. The hand-written Args parser is gone; flags are declared in the definitions, and the dispatcher validates them. Inside the layer, typing a bare path opens it, spaces and all:

src> /explorer open --path=..\docs --mode=tab
src> /explorer
src [explorer]> group add --name=work --root=C:\Work --mode=tab
src [explorer]> C:\Work\Client Project
Opened in a new tab: C:\Work\Client Project  (group: work)

Service.cs (pipe server, mutex, serve/stop) became ExplorerHost. The service now runs inside Alexander, so there's no second process to talk to. Shell COM still has to run on one STA thread, so ExplorerHost owns a dedicated STA worker thread, and every open or list is queued onto it. The old focus poller is now track --on / track --off, and it's off by default, matching how serve was opt-in. ExplorerHost is a DI singleton, so when Core unloads (on /rebuild or exit), disposing the service provider stops both threads automatically. That's what keeps a background COM thread from blocking the unload.

Small behavior fixes: Relative paths now resolve against Alexander's current folder through ICurrentFolder; the original resolved them against the process's working directory. Logging goes to Alexander's app log with an [explorer] prefix instead of manager.log. Config and state still live in %AppData%\ExplorerManager, so your existing config.json and state.json carry over.

Things to know

The registration is wrapped in OperatingSystem.IsWindows(), and the Windows-only classes carry [SupportedOSPlatform("windows")]. This connects to the CA1416 warnings from earlier. If you switch Alexander.Service to plain net8.0, this service won't generate any warnings. If you keep the -windows target, the guard is just harmless.

Anything that called explorer-manager open from outside Alexander would stop working if you retire the standalone exe. That includes shortcuts, scripts and file-association hooks. If you rely on those, keep the exe for now. The service could later get a small named-pipe listener in ExplorerHost to accept those requests.