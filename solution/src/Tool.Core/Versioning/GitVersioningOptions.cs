namespace Fuxion.Tool.Core.Versioning;

/// <summary>
/// How the version is calculated from git (<c>FxVersion</c> of the SDK, <c>fx version</c>): the repository (its folder or
/// one inside it; the current folder, without it), whether a problem is an error or falls back to the default version,
/// whether the build is taken as a CI build (no informational suffix), and the informational suffix instead of the
/// default one.
/// </summary>
public sealed record GitVersioningOptions(string? RepositoryPath = null, bool FailOnError = false, bool ForceCi = false, string? InformationalSuffix = null);
