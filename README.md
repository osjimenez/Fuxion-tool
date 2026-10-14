# Fuxion-tool

`fx`, the Fuxion workspace tool, and `Fuxion.Tools.Sdk`, the MSBuild SDK shared by the Fuxion repos.

Work in progress: extracted on 06-oct-2026 from Fuxion-plus (with its history) and being rebuilt. The design lives in
the Fuxion workspace (`_docs/plans/diseno-workspace-fx.md`); this repo follows plan K there.

## `fx`

A native executable (`fx.exe`, about 12 MB, no .NET needed to run it) built with System.CommandLine and
XenoAtom.Terminal.UI. It reads git with the `git` executable, so `git` must be on the `PATH`.

```text
fx ui                         the workspace in full screen: its tree, a panel per node and the doctor (Ctrl+Q quits)
fx sync [module] [--dry-run]  generate what fx owns here and in everything mounted below, from _fx
        [--only]              this root alone
        [--parent-ref <b>]    read the parent from its remote, on that branch
fx doctor [module]            check it, changing nothing
fx version [--explain]        the version this repository would build now, and why
fx version tag <X.Y>          create the tag version/X.Y.0 on HEAD (local; push it yourself)
fx repo list|status [--all]   the repositories of the workspace; status, only those that ask for attention
fx repo mount|unmount|pull    mount (back from the parking) or unmount (park) repositories; pull the mounted ones
fx repo add|remove|tag|set    edit the repositories of the manifest (a diff; --dry-run)
fx repo rename <name> <new>   rename one: its references here change, the old name stays in aliases
fx path add|remove            what the git of the workspace versions
fx worktree add <branch>      a worktree of a branch in the nearest workspace (--in another), with where you are
        [--in <ws>] [--mandatory]
fx worktree mount|unmount     more (or less) of the tree in a worktree: repositories, or paths left out
fx worktree list|remove       the worktrees around here; remove one (not with uncommitted changes)
fx describe                   every command, what it outputs and what it does (--output json: for agents)
fx schema [<id>]              the JSON Schema of an output (fx-sync/1…); without an id, the list
fx module enable|disable      the modules of fx in this root (a folder of _fx each)
fx                            the commands by topic and the global options (also fx --help)

Global options: --root <path>, --output human|json|ndjson, -v|--verbose, --plain, --non-interactive, --lang en|es
```

- **What fx fixes, and nothing else**: `_fx/`, the anchor of a root of fx (a repository or a workspace), with a folder
  per module (`_fx/workspace/workspace.yaml`, `_fx/dotnet/dotnet.yaml`…; a module is on if its folder is there,
  `enabled: false` turns it off); `_fx/fx.yaml` (the name of a root, `requires` and the catalog of tags);
  `_fx/.inherited/`, what a repository receives from its workspace; the shares it receives, which its `inherited.yaml`
  lists; the local files, `~$…`; and the `.gitignore` of a
  workspace (a whitelist: `*`, then `_fx/`, its `paths`, the solutions it generates, the shares it receives, the
  repositories inside versioned folders and `~$*`). Where the repositories go, what the workspace versions and which solutions it composes are the
  user's. Every YAML file starts with its own `schema` (semver); keys are kebab-case.
- **Workspaces inside workspaces**: each root inherits from its parent only, one level up (its `_fx/.inherited/` and
  its module folders), already merged (the nearest wins by name), with the repositories of `applies-to` made absolute
  (`/acme/product/app`). A repository nothing applies to gets nothing. `fx sync` works on the root where it runs and
  everything mounted below, never above: what goes down, from the top; the solutions, from the bottom up. The map of
  `FxReference` is made by the highest workspace on disk (`_fx/dotnet/~$workspace.props`). A repository whose parent
  is not on disk above it (a clone on its own) reads it from its remote, on its main branch or `--parent-ref`: a clone
  of one commit and only `_fx/`, in a cache of the user (`%LOCALAPPDATA%\fx\cache`, or `FX_CACHE`), used as it is
  with `--offline`. `fx repo rename` keeps the old name in `aliases`, so references to it keep working while they
  change (the doctor warns about them).
- **Mounted is at its place**: fx manages it, Visual Studio loads it and `FxReference` uses its projects. Unmounting
  parks it: the clone moves to `_fx/~$parking/<its path>` of its workspace, with its work, and its worktrees still
  linked. Moving is a rename on the same disk, instant whatever it holds; if something has it open, nothing moves and
  fx says so. Mounting brings it back without cloning. fx never deletes a clone.
- **Guests**: a workspace can mount a repository of another one with `inherits: false`. It uses it (its solution, the
  map), but nothing of the workspace reaches it: it inherits from its own parent. When a tree has several copies of a
  repository (the same `url`), the highest one is used, guest or not, and `fx sync` parks the others (it brings one
  back if the higher copy goes). Two copies at the same height are an error: mount it higher, or in only one of them.
- **Shares**: files a workspace gives the repositories below it, outside `_fx/` (an icon, a `LICENSE`…): `shares` in
  its `workspace.yaml`, each with its `path` (a file, or a folder ending in `/`; versioned by the workspace first: the
  doctor checks it), an optional `destination` and `applies-to`. They go down like the rest of what is inherited: a
  workspace in between keeps them in its `_fx/.inherited/workspace/` to pass them on, and a repository they apply to
  receives them at their destination, versioned. They are fx's because its `inherited.yaml` lists them (an image takes
  no header): fx updates them, removes them when they are no longer given, and never overwrites a file of the
  repository's own (unless `--adopt`). At the same destination, the nearest workspace wins.
- **Worktrees**: a branch to work on apart, without touching the main tree. `fx worktree add <branch>` creates it in
  the nearest workspace (or `--in` one above), in its `_fx/~$worktrees/<branch>/`, never inside a repository of code
  (a repository on its own, in `%LOCALAPPDATA%\fx\worktrees`, or `FX_WORKTREES`). Its root is always that workspace in
  worktree mode: a `git worktree` of its main clone, sparse (without the `paths` marked `worktree: false`). Mounting
  in it is a `git worktree add` of each repository from its main clone (cloned once, parked, if there is none), in
  the same branch and at the same path: the local branch if there is one; if not, one that follows `origin`; if not,
  a new one from HEAD. Nothing is mandatory in a worktree: it starts with its workspace and the node where you are
  (with the workspaces on the way), `fx worktree mount` adds more and `--mandatory` brings the mandatory ones down. For
  the code it is a closed tree: its own map of `FxReference` (what is not in it is a package), what is inherited read
  inside it, but its root, from the main tree; `fx sync` there neither clones nor parks. `fx worktree unmount` and
  `remove` never take out a repository with uncommitted changes, and delete the branches fx created with
  `git branch -d`. The card of each one is its `~$worktree.yaml`. The doctor warns of the worktrees out of their
  place (made by `git worktree add` or by an agent).

- **Languages**: the messages to the user are in English or Spanish: `--lang`, else `FX_LANG`, else the system's
  (Windows' UI language; `LC_ALL`, `LC_MESSAGES`, `LANG` and `LANGUAGE` elsewhere), else English. Codes, commands and
  JSON are always English. The texts are in `Resources/Strings.resx` and `Strings.es.resx` of `Tools.Core` and `Tools`;
  with native AOT both languages are inside the single exe.

- **`_fx/dotnet/dotnet.yaml`** is the single place for target frameworks, conditions and the packages that must be
  the same everywhere: `sdks` (written to `msbuild-sdks` in `global.json`), `variables` (a `value`, or a `when`
  condition), `imports` (plain MSBuild files of `_fx/dotnet/` and where they go) and `packages` (ids with one version per
  `when`; with a single version, `when` may be left out), each with its `applies-to` (tags or repositories). From what
  the root inherits and its own on top, `fx sync` generates `_fx/dotnet/dotnet.g.props` (with `FxRepositoryName` and
  `FxRepositoryFullName`) and `dotnet.g.targets`, and the versions of the packages the repo uses: with central package
  management, `_fx/dotnet/packages.g.props` (and its `<Import>` in `Directory.Packages.props`); without it,
  `PackageReference Update` items. It also composes the `solutions` the root declares, by hand, from other `.slnx`.
  `fx doctor` also checks that each package has a single owner and that exactly one version applies to every real
  target framework.
- Generated files carry a header (`GENERATED by Fuxion.Tools (fx) - DO NOT EDIT`, where they come from, the command
  that regenerates them); fx never overwrites a file without it, unless `--adopt`.

- The version comes from the git history: the last `version/X.Y.0` tag and the commits since, with one rule per
  branch kind (`main`/`master`, `develop`, `feature/*`, `release/*`, `preview/*`, any other).
- `--output json` writes one document on stdout with a versioned schema (`fx-sync/1`, `fx-doctor/1`, `fx-repo-list/1`,
  `fx-version/1`…): `schema`, `ok`, the command's data (for sync and doctor, `modules`: the files and repositories
  each module touched) and `diagnostics`. Each diagnostic has a stable code (`dotnet.outdated`, `version.no-tag`…), a
  severity (`error`, `warning`, `info`), its scope (`module`, `repository`), the message, where (`file`, `line`) and,
  when there is one, the `fix` (a `command` to run or a `hint`). Codes, commands and JSON are always in English.
- **Exit codes**, the same for every command: 0, done without errors (there may be warnings); 1, done with errors; 2,
  wrong usage (an unknown command or option, a missing value, or something fx would ask with no one to ask); 130,
  cancelled.
- **fx describes itself**: `fx describe --output json` (`fx-describe/1`) gives every command with its arguments and
  options, the schema of its JSON, whether it changes something, whether it may use the network, whether it takes
  `--dry-run` and whether it needs a person at the terminal (`fx ui`), the global options and the exit codes. `fx schema <id>` gives the JSON Schema (2020-12) of each output,
  made from the types that write it. They are the source of the skill of fx and of the documentation of the CLI.
- `--verbose` writes each step of the core on stderr as it finishes (clone, fetch, status… per repository), and the
  `info` diagnostics.
- `--output ndjson` writes one JSON event per line as the work goes on (`fx-events/1`: `start`, `step-started`,
  `step-finished`, `diagnostic`); the last line is the command's document, as with `--output json`.
- fx adapts to where it runs, without changing what it does: it only asks and animates in an interactive terminal
  (not in CI, nor in a pipe, nor for a coding agent), and only uses colours where they can be shown.
  `--non-interactive`, `--plain` and `--output` always win. Ctrl+C cancels: exit code 130.
- When a command misses what it could ask for (the repositories of `fx repo mount`, the version of `fx version tag`)
  and there is a person at the terminal, fx asks, and then shows the command that does the same without asking; Esc
  or Ctrl+C cancel. Without a terminal it says what is missing and ends with exit code 2, as any usage error.
- The commands that edit the configuration (`_fx/workspace/workspace.yaml`, the yaml of a module) change only what
  they must and keep the rest of the file: comments, order, quoting and line endings. They show the change as a diff,
  check it as the file is read (an invalid one is not written) and write it, unless `--dry-run` (`fx-manifest-edit/1`
  with `--output json`). They never touch clones nor folders: `fx sync` does the rest, and they say so.
- In a terminal, the work that takes a while (git on every repository) shows a spinner per step as it runs; the long
  steps (clone, fetch, pull) and the failed ones stay as lines above it. `fx doctor` shows a line per module (`[✓]`,
  `[!]`, `[✗]`), each problem under it with its fix, and a summary. Tables have borders in a terminal and are plain
  columns elsewhere.
- **`fx ui`** shows the workspace in full screen, from the highest workspace on disk, with the node where it was
  launched selected. On the left, one tree with what is on disk and what the manifests declare: the repositories not
  mounted or not cloned, parked or superseded by a higher copy, what nobody declares (`?`), the modules (also the ones
  not used, dimmed), and whose each file is (generated, inherited and from where, a share, local, by hand). Inside a
  repository, only what matters to fx; F3 shows the rest and the local files. git (`↑` ahead, `↓` behind, `*`
  changes) and the doctor (`!` warnings, `✗` errors, `✓`) put their marks on each node, and the problems go up to its
  parents; both run in the background, without network, and F5 runs them again. F2 opens the list of problems (Enter
  goes to its node). On the right, a panel per node: a workspace (its repositories, problems, `paths` and shares,
  worktrees), a repository (its state and last commits, what reaches it from its workspaces and why, its problems), a
  module (whom the .NET of a workspace reaches), a file (whose it is, where it comes from and its content; on
  `fx.yaml`, the catalog of tags). F4 goes where a file comes from: an inherited one, to the `_fx/` of the nearest
  workspace it comes from; a share, to its source; a generated one, to the yaml it is made of. Enter on a worktree of
  the panel of its workspace opens it as its own tree, with its branch on top (F6 goes to the main tree). Alt+← goes
  back. It changes nothing yet: each panel shows the command that does the same. Only in a terminal with a person:
  elsewhere (a pipe, CI, an agent, `--output json`) it says so and ends with exit code 2.

## `Fuxion.Tools.Sdk`

An MSBuild SDK for the tool's own features. A repo pins it in the `global.json` at its root and imports it in
`Directory.Build.props`, after the properties of the area:

```json
{ "msbuild-sdks": { "Fuxion.Tools.Sdk": "0.1.0" } }
```

```xml
<Project>
	<Import Project="Sdk.props" Sdk="Fuxion.Tools.Sdk" />
</Project>
```

**Minimally intrusive**: it adds `Fx*` settings for its own features and decides nothing else. The language version,
nullable, test frameworks, PolySharp, packaging metadata… are the repo's, in plain props and targets.

- **Version from git**, the same as `fx version`, set before anything reads it (assembly attributes, package):
  calculated once per repository and build. `FxVersioningEnabled=false` turns it off.
- **Static metadata**: `Fuxion.Metadata.<Project>.{Project, Build, Versioning, Repository, Json}`, generated in `obj/`. The
  build date is the date of the HEAD commit (or `SOURCE_DATE_EPOCH`), so the file only changes with the commit.
- **Where it is**: `FxRepositoryRoot` (where fx generated its .NET, or its `global.json`), `FxWorkspaceRoot` (the
  highest workspace on disk; the root of a worktree stops it), `FxRepositoryName` and `FxRepositoryFullName` (as its
  workspace declares it). With them, the repo's own props decide, for example, where its packages go when publishing
  locally (a `PackageOutputPath`); the SDK does not.
- **What `fx` generates from `_fx/dotnet/dotnet.yaml`**: its variables (`$(CoreFrameworks)`, `$(IsNet11)`,
  compilation constants) and its **imports**, plain MSBuild files imported where a condition holds. `props:` go before
  the project file; `targets:` right after it, before the .NET SDK computes anything, so they see what the project
  declares. A test project only says `<IsTestProject>true</IsTestProject>`, and the repo's `test.targets` makes it an
  executable with its test framework:

  ```yaml
  imports:
    - props: default.props              # every project
    - targets: test.targets             # OutputType=Exe, xunit.v3…
      when: "'$(IsTestProject)' == 'true'"
  ```

- **References between repositories**: `<FxReference Include="Project" />`, a project inside a workspace, a package
  outside it.
- **Translated XML docs** (`FxDocumentationLanguages`), only when asked for.
- fx's local files and folders (`~$…`) are not part of any project.
- The tasks run on the .NET task host of MSBuild: in process under `dotnet build`, out of process under Visual Studio
  (its MSBuild is .NET Framework). That is why they, and `Fuxion.Tools.Core`, target `net10.0`.

The settings are listed at the top of `solution/src/Tools.Sdk/Sdk/Sdk.props`.

## Layout

- `FxTool.slnx` at the root; projects under `solution/src` (`Fuxion.Tools`, the `fx` command; `Fuxion.Tools.Core`,
  the logic; `Fuxion.Tools.Sdk`, the SDK) and tests under `solution/test`.
- `solution/Directory.Build.props` and `solution/Directory.Packages.props` are provisional: this repo does not use its
  own SDK yet.
- `solution/sketches/NugetPackageManager` (list, unlist and deprecate Fuxion packages on nuget.org) is in no solution
  yet: it needs Fuxion 11.
- `solution/dev` has tools to work on `fx`, not tests: `Pty`, a harness to try it in a real terminal without a person
  (Windows: a pseudoconsole; how to use it, at the top of its `Program.cs`), and `resx.pl`, which adds, changes or
  removes a text in English and Spanish at once (`perl solution/dev/resx.pl <data.tsv> <Strings.resx>
  <Strings.es.resx>`, with lines `key<TAB>english<TAB>spanish`, or `-key`).
- `solution/.config` keeps the MSBuild targets that integrate the old tool in the Fuxion-plus build, which the SDK
  replaces.

## Build, test, publish

```bash
dotnet build FxTool.slnx
dotnet test --solution FxTool.slnx
dotnet publish solution/src/Tools/Fuxion.Tools.csproj -c Release -r win-x64 -o <folder>
```

**Versions**: this repo versions itself from its `version/X.Y.0` tags (`fx version`; `main` gives `0.1.{commits}`).
While it does not build with its own SDK, the version is passed when packing or publishing.

**Publishing locally** (inside the Fuxion workspace, design D-36):

```bash
V=$(fx version)
dotnet pack solution/src/Tools.Sdk/Fuxion.Tools.Sdk.csproj -c Release -p:Version=$V -o ../../~\$publish/tool/nupkgs
dotnet publish solution/src/Tools/Fuxion.Tools.csproj -c Release -r win-x64 -p:Version=$V -o ../../~\$publish/tool/fx/win-x64
```

Publish from a clean commit: NuGet caches packages by version, so the same version must not change.

The tests build throwaway git repositories under the temp folder with the real `git`, isolated from the user's git
configuration; the SDK tests pack the SDK and build sample repositories with it (`dotnet build` and, if Visual
Studio is installed, its `MSBuild.exe`). `fx ui` is tested on a terminal in memory: the tests push keys and read the
screen back. Publishing needs the C++ build tools of Visual Studio (native AOT); from Git Bash, add
`C:\Program Files (x86)\Microsoft Visual Studio\Installer` to the `PATH` so the linker finds `vswhere.exe`.

Licensed under the [MIT license](LICENSE).
