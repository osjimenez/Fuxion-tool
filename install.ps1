# Installs fx, the tool of the Fuxion workspace, from the releases of Fuxion-tool on GitHub (plan S):
#
#   irm https://raw.githubusercontent.com/osjimenez/Fuxion-tool/main/install.ps1 | iex
#
# With options (a version, another folder):
#
#   & ([scriptblock]::Create((irm https://raw.githubusercontent.com/osjimenez/Fuxion-tool/main/install.ps1))) -Version 0.7.0 -InstallDir C:\tools\fx
#
# It downloads the archive of this platform (win-x64 or win-arm64) and SHA256SUMS, checks the archive, extracts it into
# the folder and adds the folder to the PATH of the user. Running it again updates fx. Windows PowerShell 5.1 or later.
param(
	# A version (0.7.0); the latest release by default.
	[string]$Version,
	# Where fx goes.
	[string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'fx\bin')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$repository = 'osjimenez/Fuxion-tool'

# the platform: the architecture of the system, not of this PowerShell (an x64 one runs on ARM64 too)
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$rid = switch ($architecture) {
	'X64' { 'win-x64' }
	'Arm64' { 'win-arm64' }
	default { throw "fx has no build for Windows on $architecture." }
}

# the release: the latest, or the one of the version asked for (its tag is release/<version>)
$headers = @{ 'User-Agent' = 'fx-install'; 'Accept' = 'application/vnd.github+json' }
$api = if ($Version) { "https://api.github.com/repos/$repository/releases/tags/release/$Version" } else { "https://api.github.com/repos/$repository/releases/latest" }
$release = Invoke-RestMethod -Uri $api -Headers $headers
$found = $release.tag_name -replace '^release/', ''
$name = "fx-$found-$rid.zip"
$archive = $release.assets | Where-Object { $_.name -eq $name }
$sums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS' }
if (-not $archive -or -not $sums) { throw "The release $($release.tag_name) has no $name or SHA256SUMS." }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('fx-install-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
	Write-Host "Downloading fx $found ($rid)..."
	$zip = Join-Path $temp $name
	Invoke-WebRequest -Uri $archive.browser_download_url -OutFile $zip -UseBasicParsing
	Invoke-WebRequest -Uri $sums.browser_download_url -OutFile (Join-Path $temp 'SHA256SUMS') -UseBasicParsing

	# the archive must be the one of SHA256SUMS ("<hash>  <file>" per line)
	$line = Get-Content (Join-Path $temp 'SHA256SUMS') | Where-Object { ($_ -split '\s+', 2)[1] -replace '^\*', '' -eq $name } | Select-Object -First 1
	if (-not $line) { throw "SHA256SUMS has no line for $name." }
	$expected = ($line -split '\s+', 2)[0].ToLowerInvariant()
	$actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
	if ($actual -ne $expected) { throw "The checksum of $name does not match SHA256SUMS: not installed." }

	New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
	Expand-Archive -Path $zip -DestinationPath $InstallDir -Force
}
finally {
	Remove-Item -Path $temp -Recurse -Force -ErrorAction SilentlyContinue
}

# the folder, in the PATH of the user (and of this session)
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$entries = @($userPath -split ';' | Where-Object { $_ })
if ($entries -notcontains $InstallDir) {
	[Environment]::SetEnvironmentVariable('Path', (($entries + $InstallDir) -join ';'), 'User')
	Write-Host "$InstallDir added to the PATH of the user (new terminals see it)."
}
if (($env:Path -split ';') -notcontains $InstallDir) { $env:Path = "$env:Path;$InstallDir" }

& (Join-Path $InstallDir 'fx.exe') --version
Write-Host "fx $found installed in $InstallDir."
