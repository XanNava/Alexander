Everything between Core and the services goes through one interface, IServiceCommandLayer, plus a small set of shared types. Here's the shape:

Core and the services touch in five places.

1. Registration: Core.CoreEntryPoint.cs. Create() calls .AddAlexanderFolderServices() and .AddAlexanderAi(). Both are extension methods defined in the services themselves (Automation.ServiceCollectionExtensions.cs and AI.ServiceCollectionExtensions.cs). Each one registers its own internals plus one IServiceCommandLayer. This is the only place Core names a specific service.

(see ServiceContractExample.png)

2. Discovery: Core.CommandCatalog.cs and Core.CommandDispatcher.cs. Both constructors take IEnumerable<IServiceCommandLayer>, which DI fills with every registered layer.

The catalog turns each layer into catalog entries: /ai, /ai ask, /ai set, …, all in an ai section. That's how /help and tab completion learn about them.
The dispatcher wraps each layer in a ServiceLayerHandler and maps those command texts to it.

3. Runtime calls (the arrows in the diagram):

The Shell calls IDT.HandleInputAsync.
CommandLayer.RouteAsync sends the input to CommandDispatcher.DispatchAsync, which parses it and validates flags against the definition.
ServiceLayerHandler.ExecuteAsync either enters the layer (bare /ai) or strips the prefix.
IServiceCommandLayer.ExecuteAsync runs inside the service, which then uses its own internals (IAiClient, IServiceRegistry).

The one shortcut: free text typed inside a layer goes straight from CommandLayer to layer.HandleTextAsync.

4. Calls from the services back up: through contracts only. Services never reference Core. They see:

the current folder through ICurrentFolder, which Core's CurrentFolderState implements;
output and state through IOutputWriter and IStateStore from Alexander.Shared;
DtResult from Alexander.Contracts, which they return.

5. Control transfer: Core.ActiveLayerState.cs. This one object holds "which layer has control":

ServiceLayerHandler sets it when you type /ai.
LifecycleCommandHandlers clears it on /exit.
CommandLayer reads it for routing, the prompt label and completion.

Where the boundary still leaks: Core still calls Automation directly in two places that bypass the contract. CoreCommandHandlers uses IServiceRegistry.RunningInstances for /state, and CommandLayer.DisposeAsync calls serviceRegistry.StopAllAsync on shutdown. Both work, but they mean Core has to know Automation exists.

To close that gap, I could add two optional members to IServiceCommandLayer, with defaults like HandleTextAsync has: a status method that /state asks every layer for, and a stop method that DisposeAsync calls on every layer. Core would then depend only on the contract, and any future service could show up in /state and shut down cleanly with no Core changes. Want me to make that change?