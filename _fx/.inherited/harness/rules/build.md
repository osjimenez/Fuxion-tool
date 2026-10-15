## Build and tests

- **How the build works** (plan N, decisions 15-18; plan P): each repository imports `Fuxion.Tools.Sdk` in its
  `Directory.Build.props`; what the repositories share is in the workspace's `_fx/dotnet/` (`dotnet.yaml` and its
  files), which `fx sync` gives each repository it applies to (`applies-to: tags [fuxion-build]`) in its
  `_fx/.inherited/` (versioned, so a loose clone builds; `inherited.yaml` says who the repository is and where each file
  comes from) and turns into its `_fx/dotnet/*.g.*`. Change the shared build there and run `fx sync`; never edit `.g.`
  files or `_fx/.inherited/`. A repository's own `_fx/dotnet/dotnet.yaml` adds to it (`oss`, `plus` and `tool`:
  `standalone: true`). Cross-repository references are `<FxReference Include="Project" />`; test projects say
  `<IsTestProject>true</IsTestProject>`. The packages go to `~$publish/<repo>/nupkgs` of the workspace.
- **Build and test** each repository with the solution at its root (`dotnet build <solution>.slnx`,
  `dotnet test --solution <solution>.slnx`), or everything from the workspace root (`Fuxion.slnx`: 69 projects,
  0 warnings). `oss` must build on its own. Tests with databases need Docker running.
- The **`build-system`** skill explains the build (sections 1-8) and the `tool` repository (section 9).
