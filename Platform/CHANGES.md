# Changes to the main Alexander solution

Only files that are new or changed are included, laid out by project.
Folder names follow the guesses used elsewhere (`Alexander.Core`,
`Alexander.Shell`); map them onto your real project folders.

## New projects (copy whole)

| Project | Purpose |
|---|---|
| `Alexander.Graphics` | Shared, UI-framework-neutral graphics library: `IGraphics`, `ServiceView`, widgets, and remote rendering (`RecordingGraphics`, `DrawOp`, `DisplayList`, `TextMetrics`). |
| `Alexander.Hosting` | The GUI <-> Shell protocol (JSON lines over stdin/stdout). Referenced by the Shell and the GUI only. |

Add both to the solution.

## Alexander.Contracts (new files)

- `Contracts.IOutputSink.cs` - where a Core instance writes output when the host isn't a console.
- `Contracts.ISessionInfo.cs` - current folder / active layer, read by the Shell's host mode.

Contracts is shared with the Shell, so rebuild and restart the Shell after adding these.

## Alexander.Service

New folders (copy whole):

- `Contracts/` - the service contract layer: `IServiceCommandLayer`, `ICurrentFolder`, `CommandDefinition`/`CommandFlag`, `ParsedCommand`/`CommandValidationException` (moved out of Core).
- `Explorer/` - ExplorerManager converted into a service (Windows-only; not registered elsewhere).

New or replaced files:

- `AI/AI.AiCommandLayer.cs` - new (replaces Core's `AiCommandHandlers`).
- `AI/AI.ServiceCollectionExtensions.cs` - new (`AddAlexanderAi`, moved out of Core).
- `Automation/Automation.AutomationCommandLayer.cs` - new (replaces Core's `ServiceCommandHandlers`).
- `Automation/Automation.AutomationStatusView.cs` - new (example custom service view).
- `Automation/Automation.ServiceCollectionExtensions.cs` - new (`AddAlexanderFolderServices`, moved out of Core).

Unchanged: `AI.AiClient.cs`, `AI.ChatMessage.cs`, and the existing Automation files
(`ServiceRegistry`, `IFolderService`, `RepeatingCommandService`, `FileWatcherService`).
Two of those have stale comments pointing at Core's `ServiceCollectionExtensions`;
they now live in `AutomationServiceCollectionExtensions` - cosmetic only.

## Alexander.Core

New files:

- `Core.GlobalUsings.cs` - imports `Alexander.Services.Contracts` everywhere in Core.
- `Core.ActiveLayerState.cs` - which service layer has control.
- `Core.ServiceLayerHandler.cs` - adapts an `IServiceCommandLayer` to the dispatcher.
- `Core.SinkOutputWriter.cs` - `IOutputWriter` over an `IOutputSink` (host mode / GUI).

Replaced files:

- `Core.CommandCatalog.cs` - built-ins + every registered service layer; string sections. (Its old value/parsing types moved to `Alexander.Service/Contracts`.)
- `Core.CommandDispatcher.cs` - wraps service layers; passes canonical command text (fixes `/LS` etc.).
- `Core.CommandLayer.cs` - routing between the shell layer and an entered service layer; prompt and completion follow the active layer.
- `Core.CoreCommandHandlers.cs` - `/help --section` accepts any registered section.
- `Core.LifecycleCommandHandlers.cs` - `/exit` leaves a service layer first; `/quit` always exits.
- `Core.CurrentFolderState.cs` - implements `ICurrentFolder`.
- `Core.ServiceCollectionExtensions.cs` - service registrations moved to the services.
- `Core.CoreEntryPoint.cs` - registers Explorer; adds `CreateWithOutput`; the returned instance also implements `ISessionInfo` and `IServiceViewProvider`.
- `Core.ICommandHandler.cs` - comment update only.
- `Core.FileCommandHandlers.cs` - *optional*: the registry-pattern version (behaves the same as the switch version).

Delete:

- `Core.AiCommandHandlers.cs`
- `Core.ServiceCommandHandlers.cs`

## Alexander.Shell

- `Shell.CoreHost.cs` - replaced: optional `Output` sink (uses `CreateWithOutput` when set) and `CurrentOrNull`.
- `Shell.HostSession.cs` - new: host mode for the GUI.

## Project references to add

| Project | Add references to |
|---|---|
| `Alexander.Service` | `Alexander.Graphics`, `Alexander.Contracts` (for `DtResult`, if not already) |
| `Alexander.Core` | `Alexander.Graphics` |
| `Alexander.Shell` | `Alexander.Graphics`, `Alexander.Hosting` |

## Edits I couldn't make (files not seen)

1. **`PluginLoadContext`** (Shell): share `Alexander.Graphics` with the default context,
   the same way `Alexander.Contracts` is shared, so `ServiceView` types match across the
   Core boundary.
2. **Shell `Program.Main`**: add the host-mode branch - see the comment at the top of
   `Shell.HostSession.cs`. In short:

       if (HostSession.IsRequested(args)) {
           await using var session = HostSession.Attach();
           coreHost.Output = session.Output;
           // existing build + coreHost.LoadAsync(...), token: session.Stopping
           // on failure: session.ReportStage(HostStages.Failed, message); return 1;
           return await session.RunAsync(coreHost, (progress, token) =>
               /* existing rebuild: build, then coreHost.SwapAsync(...) */);
       }

3. **`ShellCommandRunner`** (Shared): for macOS/Linux it must run commands through
   `/bin/sh -c` rather than `cmd /c`.
