# Explorer Manager (C#)

Routes "open this folder" requests to File Explorer using per-folder-group rules:
open in a new tab of the group's window, reuse the group's tab, or open a new window.

## Open in Visual Studio

Open `ExplorerManager.sln` in Visual Studio 2022 (17.8 or newer, with the ".NET desktop development" workload).
The run button's dropdown has debug profiles (`serve`, `list`, `open`, `group list`); edit them in
Project > Properties > Debug.

To get a single exe: right-click the project > Publish > FolderProfile > Publish.
Output: `ExplorerManager\bin\Release\publish\explorer-manager.exe`.

## Build from the command line

Requires the .NET 8 SDK on Windows.

```
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Result: `bin\Release\net8.0-windows\win-x64\publish\explorer-manager.exe`
(add `--self-contained true` if the target PCs don't have the .NET 8 runtime).

## Use

```
explorer-manager group add Projects "D:\Projects" --mode tab
explorer-manager group add Photos "E:\Photos" --mode navigate
explorer-manager default --mode new_window
explorer-manager serve          (put a shortcut to this in shell:startup)
explorer-manager open "D:\Projects\app\src"
explorer-manager list
```

Other programs just run `explorer-manager.exe open "<folder>"` instead of `explorer.exe "<folder>"`.

Files live in `%APPDATA%\ExplorerManager\`: `config.json` (rules, reread on every request),
`state.json` (last window per group, window MRU), `manager.log`.

## config.json

```json
{
  "default": { "mode": "new_window", "focus_if_open": true, "avoid_group_windows": true },
  "groups": [
    { "name": "Projects", "root": "D:\\Projects", "mode": "tab" },
    { "name": "ClientX",  "root": "D:\\Projects\\ClientX", "mode": "new_window", "focus_if_open": false }
  ]
}
```

The longest matching root wins, so `ClientX` overrides `Projects` for its own subfolders.
