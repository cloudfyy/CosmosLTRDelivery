#Requires -Version 7.2
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'tool\publish'),

    [string] $RuntimeIdentifier = 'linux-x64',

    [switch] $SelfContained
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'tool\source\LTRTool\LTRTool.csproj'
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "LTRTool project was not found: $project"
}
$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
$sourceRoot = Join-Path $PSScriptRoot 'tool\source'
Push-Location -LiteralPath $sourceRoot
try {
    & dotnet publish $project `
        --configuration Release `
        --runtime $RuntimeIdentifier `
        --self-contained $selfContainedValue `
        --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
