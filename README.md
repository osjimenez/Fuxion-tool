# Fuxion-tool

`fx`, the Fuxion workspace tool, and `Fuxion.Tools.Sdk`, the MSBuild SDK shared by the Fuxion repos.

Work in progress: extracted on 06-oct-2026 from Fuxion-plus (with its history) and being rebuilt. The design lives in
the Fuxion workspace (`_docs/plans/diseno-workspace-fx.md`); this repo follows plan K there.

## `fx`

A native executable (`fx.exe`, about 5 MB, no .NET needed to run it) built with System.CommandLine and
Spectre.Console. It reads git with the `git` executable, so `git` must be on the `PATH`.

```text
fx version [--explain]      the version this repository would build now, and why
fx version tag <X.Y>        create the tag version/X.Y.0 on HEAD (local; push it yourself)

Global options: --root <path>, --output human|json, --verbose, --plain
```

- The version comes from the git history: the last `version/X.Y.0` tag and the commits since, with one rule per
  branch kind (`main`/`master`, `develop`, `feature/*`, `release/*`, `preview/*`, any other).
- `--output json` writes one document on stdout with a versioned schema (`fx-version/1`, `fx-version-tag/1`) and the
  diagnostics, each with a stable code (`version.no-tag`…). Exit code 1 only with errors.

## Layout

- `FxTool.slnx` at the root; projects under `solution/src` (`Fuxion.Tools`, the `fx` command; `Fuxion.Tools.Core`,
  the logic; `Fuxion.Tools.MSBuild`, empty, the future `Fuxion.Tools.Sdk`) and tests under `solution/test`.
- `solution/Directory.Build.props` and `solution/Directory.Packages.props` are provisional until `Fuxion.Tools.Sdk` is
  published.
- `solution/sketches/NugetPackageManager` (list, unlist and deprecate Fuxion packages on nuget.org) is in no solution
  yet: it needs Fuxion 11.
- `solution/.config` keeps the MSBuild targets that integrate the tool in the Fuxion build today: the seed of the SDK.

## Build, test, publish

```bash
dotnet build FxTool.slnx
dotnet test --solution FxTool.slnx
dotnet publish solution/src/Tools/Fuxion.Tools.csproj -c Release -r win-x64 -o <folder>
```

The tests build throwaway git repositories under the temp folder with the real `git`, isolated from the user's git
configuration. Publishing needs the C++ build tools of Visual Studio (native AOT); from Git Bash, add
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` to the `PATH` so the linker finds `vswhere.exe`.

Licensed under the [MIT license](LICENSE).
