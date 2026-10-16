<br/><br/>
<p align="center">
  <image src="https://raw.githubusercontent.com/osjimenez/Fuxion/refs/heads/main/solution/.config/full_logo.svg" alt="Fuxion logo" width="300px">
</p>
<br/><br/>

Usage, in the `global.json` of the repository:
```json
{ "msbuild-sdks": { "Fuxion.Tools.Sdk": "<version>" } }
```
and in its `Directory.Build.props`:
```xml
<Import Project="Sdk.props" Sdk="Fuxion.Tools.Sdk" />
```

**🔨 Work in progress..**
