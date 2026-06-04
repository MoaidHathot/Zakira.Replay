param(
    [string]$ApiKey,
    [switch]$Push,
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "artifacts",
    [string]$Source = "https://api.nuget.org/v3/index.json"
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$solution = Join-Path $repoRoot "Zakira.Replay.slnx"
$output = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }

if (Test-Path -LiteralPath $output) {
    Get-ChildItem -LiteralPath $output -File -Filter "*.nupkg" | Remove-Item -Force
    Get-ChildItem -LiteralPath $output -File -Filter "*.snupkg" | Remove-Item -Force
}

"Packing $solution -> $output ($Configuration)"
& dotnet pack $solution -c $Configuration -o $output
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE"
}

$nupkgs = @(Get-ChildItem -LiteralPath $output -File -Filter "*.nupkg" | Where-Object { $_.Name -notlike "*.symbols.nupkg" })

if (-not $nupkgs -or $nupkgs.Count -eq 0) {
    throw "No .nupkg produced under $output"
}

""
"Produced packages:"
foreach ($file in (Get-ChildItem -LiteralPath $output -File | Sort-Object Name)) {
    "  {0,12:N0}  {1}" -f $file.Length, $file.Name
}

if (-not $Push) {
    ""
    "Pack complete. Re-run with -Push to publish to $Source."
    return
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = $env:NUGET_API_KEY
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw "API key not provided. Pass -ApiKey <key> or set the NUGET_API_KEY environment variable."
}

""
foreach ($nupkg in $nupkgs) {
    "Pushing $($nupkg.Name) to $Source"
    & dotnet nuget push $nupkg.FullName --api-key $ApiKey --source $Source
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet nuget push failed for $($nupkg.Name) with exit code $LASTEXITCODE"
    }
}

""
"Push complete."
