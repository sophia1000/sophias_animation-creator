param(
    [string]$RepositoryUrl = "https://raw.githubusercontent.com/sophia1000/sophias_animation-creator/main/vpm.json",
    [string]$PackageBaseUrl = "https://raw.githubusercontent.com/sophia1000/sophias_animation-creator/main/dist",
    [string]$RepositoryName = "Sophia's VPM Packages",
    [string]$RepositoryId = "com.sophia.vpm",
    [string]$AuthorName = "Sophia <sophia1000@users.noreply.github.com>"
)

$ErrorActionPreference = "Stop"

function Get-JsonProperty {
    param(
        [Parameter(Mandatory = $true)] [object]$Object,
        [Parameter(Mandatory = $true)] [string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)] [object]$Object,
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $false)] [object]$Value
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
    }
    else {
        $property.Value = $Value
    }
}

$PackageRoot = Split-Path -Parent $PSScriptRoot
$PackageJsonPath = Join-Path $PackageRoot "package.json"
$RepositoryJsonPath = Join-Path $PackageRoot "vpm.json"
$DistPath = Join-Path $PackageRoot "dist"

if (-not (Test-Path -LiteralPath $PackageJsonPath)) {
    throw "package.json was not found at $PackageJsonPath"
}

$PackageJsonRaw = Get-Content -LiteralPath $PackageJsonPath -Raw
$Package = $PackageJsonRaw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($Package.name) -or [string]::IsNullOrWhiteSpace($Package.version)) {
    throw "package.json must contain name and version."
}

New-Item -ItemType Directory -Force -Path $DistPath | Out-Null

$ZipFileName = "$($Package.name)-$($Package.version).zip"
$ZipPath = Join-Path $DistPath $ZipFileName
$StagePath = Join-Path ([System.IO.Path]::GetTempPath()) "$($Package.name)-vpm-stage"

if (Test-Path -LiteralPath $StagePath) {
    Remove-Item -LiteralPath $StagePath -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $StagePath | Out-Null

$ExcludedTopLevelItems = @(".git", ".github", "Tools", "dist", "vpm.json")
Get-ChildItem -LiteralPath $PackageRoot -Force | Where-Object {
    $ExcludedTopLevelItems -notcontains $_.Name
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $StagePath -Recurse -Force
}

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Compress-Archive -Path (Join-Path $StagePath "*") -DestinationPath $ZipPath -Force
$ZipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()

$VersionManifest = $PackageJsonRaw | ConvertFrom-Json
Set-JsonProperty $VersionManifest "url" "$PackageBaseUrl/$ZipFileName"
Set-JsonProperty $VersionManifest "zipSHA256" $ZipHash

if (Test-Path -LiteralPath $RepositoryJsonPath) {
    $Repository = Get-Content -LiteralPath $RepositoryJsonPath -Raw | ConvertFrom-Json
}
else {
    $Repository = [PSCustomObject]@{}
}

Set-JsonProperty $Repository "name" $RepositoryName
Set-JsonProperty $Repository "id" $RepositoryId
Set-JsonProperty $Repository "url" $RepositoryUrl
Set-JsonProperty $Repository "author" $AuthorName

$Packages = Get-JsonProperty $Repository "packages"
if ($null -eq $Packages) {
    $Packages = [PSCustomObject]@{}
    Set-JsonProperty $Repository "packages" $Packages
}

$PackageEntry = Get-JsonProperty $Packages $Package.name
if ($null -eq $PackageEntry) {
    $PackageEntry = [PSCustomObject]@{
        versions = [PSCustomObject]@{}
    }
    Set-JsonProperty $Packages $Package.name $PackageEntry
}

$Versions = Get-JsonProperty $PackageEntry "versions"
if ($null -eq $Versions) {
    $Versions = [PSCustomObject]@{}
    Set-JsonProperty $PackageEntry "versions" $Versions
}

Set-JsonProperty $Versions $Package.version $VersionManifest

$Repository | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $RepositoryJsonPath -Encoding UTF8

Remove-Item -LiteralPath $StagePath -Recurse -Force

Write-Host "Built $ZipPath"
Write-Host "Updated $RepositoryJsonPath with $($Package.name) $($Package.version)"

