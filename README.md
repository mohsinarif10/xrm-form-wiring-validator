# Form Wiring Validator

An [XrmToolBox](https://www.xrmtoolbox.com/) tool that finds **broken form script wiring** in a
Dataverse / Dynamics 365 environment.

These problems are silent. The solution imports cleanly, nothing is logged, and the first
sign of trouble is an error dialog in a user's face — or a handler that simply never runs.

## What it checks

| Check | Problem it catches |
|---|---|
| Function exists | A form calls `Namespace.doThing`, but the declared library does not define `doThing` |
| Library exists | A handler points at a web resource that is missing or empty |
| Library declared | A handler uses a library that is not in the form's library list |
| Column exists | A control is bound to a column that no longer exists on the table |

## Scope and noise

Every environment contains hundreds of Microsoft's own forms whose scripts are bundled inside the
platform. Checking those produces false positives and nothing else, so the tool works on **your own
publisher prefixes** and leaves Microsoft's components alone. Prefixes are detected automatically and
you choose which ones to scan.

## Results

Findings are listed with the table, form, handler, library and the reason, and can be exported to CSV
so they can be pasted straight into a work item.

## Building

- Visual Studio 2022 or later
- .NET Framework 4.8
- `dotnet build` or build the solution, then copy the output DLL into the XrmToolBox `Plugins` folder

## Licence

MIT — see [LICENSE](LICENSE).
