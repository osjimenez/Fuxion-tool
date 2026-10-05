# Fuxion-tool

`fx`, the Fuxion workspace tool, and `Fuxion.Tools.Sdk`, the MSBuild SDK shared by the Fuxion repos.

Work in progress: extracted on 06-oct-2026 from Fuxion-plus (with its history) and being rebuilt. The design lives in
the Fuxion workspace (`_docs/plans/diseno-workspace-fx.md`); this repo follows plan K there.

- `FxTool.slnx` at the root; projects under `solution/src`.
- `solution/Directory.Build.props` and `solution/Directory.Packages.props` are provisional until `Fuxion.Tools.Sdk` is
  published.
- `solution/sketches/NugetPackageManager` (list, unlist and deprecate Fuxion packages on nuget.org) is in no solution
  yet: it needs Fuxion 11.
- `solution/.config` keeps the MSBuild targets that integrate the tool in the Fuxion build today: the seed of the SDK.

Licensed under the [MIT license](LICENSE).
