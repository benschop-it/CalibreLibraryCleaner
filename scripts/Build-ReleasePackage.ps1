[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',

    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'bin\releases'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$runtimeIdentifier = 'win-x64'
$deployment = 'framework-dependent'
$packageName = "CalibreLibraryCleaner-$Version-$runtimeIdentifier-$deployment"
$workRoot = Join-Path $repositoryRoot "bin\release-work\$packageName"
$wpfPublish = Join-Path $workRoot 'publish-wpf'
$pdfPublish = Join-Path $workRoot 'publish-pdf-worker'
$payload = Join-Path $workRoot $packageName
$pdfPayload = Join-Path $payload 'pdf-worker'
$zipPath = Join-Path $OutputRoot "$packageName.zip"
$manifestPath = Join-Path $payload 'release-manifest.json'
$utf8NoBom = New-Object Text.UTF8Encoding($false)

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Get-RelativePackagePath {
    param([string]$Root, [string]$Path)
    return $Path.Substring($Root.Length + 1).Replace('\', '/')
}

if (Test-Path -LiteralPath $workRoot) {
    Remove-Item -LiteralPath $workRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $wpfPublish, $pdfPublish, $payload, $pdfPayload -Force | Out-Null
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$wpfProject = Join-Path $repositoryRoot 'src\CalibreLibraryCleaner.Wpf\CalibreLibraryCleaner.Wpf.csproj'
$pdfProject = Join-Path $repositoryRoot 'src\CalibreLibraryCleaner.PdfWorker\CalibreLibraryCleaner.PdfWorker.csproj'
Invoke-DotNet restore $wpfProject --runtime $runtimeIdentifier
Invoke-DotNet restore $pdfProject --runtime $runtimeIdentifier
$publishProperties = @(
    '--configuration', 'Release',
    '--runtime', $runtimeIdentifier,
    '--self-contained', 'false',
    '--no-restore',
    '-p:PublishSingleFile=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:ContinuousIntegrationBuild=true',
    "-p:Version=$Version"
)
Invoke-DotNet publish $wpfProject @publishProperties --output $wpfPublish
Invoke-DotNet publish $pdfProject @publishProperties --output $pdfPublish

Copy-Item -Path (Join-Path $wpfPublish '*') -Destination $payload -Recurse -Force
Copy-Item -Path (Join-Path $pdfPublish '*') -Destination $pdfPayload -Recurse -Force

$required = @(
    'CalibreLibraryCleaner.Wpf.exe',
    'CalibreLibraryCleaner.Wpf.dll',
    'CalibreLibraryCleaner.Wpf.deps.json',
    'CalibreLibraryCleaner.Wpf.runtimeconfig.json',
    'CalibreLibraryCleaner.Infrastructure.dll',
    'pdf-worker/CalibreLibraryCleaner.PdfWorker.exe',
    'pdf-worker/CalibreLibraryCleaner.PdfWorker.dll',
    'pdf-worker/CalibreLibraryCleaner.PdfWorker.deps.json',
    'pdf-worker/CalibreLibraryCleaner.PdfWorker.runtimeconfig.json'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $relative.Replace('/', '\')) -PathType Leaf)) {
        throw "Required release file is missing: $relative"
    }
}

$forbiddenName = '(?i)(\.pdb$|\.log$|\.tmp$|\.cache(?:[-./]|$)|\.user$|\.suo$|testhost|(^|/)(?:test|tests|cache|caches|log|logs|credential|credentials)(/|$)|\.tests?\.|api.?key|library-state|library-snapshot|metadata-cover-staging)'
$payloadFiles = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
foreach ($file in $payloadFiles) {
    $relative = Get-RelativePackagePath $payload $file.FullName
    if ($relative -match $forbiddenName) {
        throw "Forbidden release file: $relative"
    }
    if ($file.Extension -in @('.json', '.config', '.xml', '.txt')) {
        $text = [IO.File]::ReadAllText($file.FullName)
        $containsRepositoryPath = $text.IndexOf(
            $repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
        $containsUserPath = -not [string]::IsNullOrWhiteSpace($env:USERPROFILE) -and
            $text.IndexOf($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase) -ge 0
        if ($containsRepositoryPath -or $containsUserPath) {
            throw "Release text contains a source-machine path: $relative"
        }
    }
    if ($file.Extension -in @('.exe', '.dll')) {
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
            throw "Release binary is not a valid PE image: $relative"
        }
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        $unicode = [Text.Encoding]::Unicode.GetString($bytes)
        $containsRepositoryPath = $ascii.IndexOf($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $unicode.IndexOf($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
        $containsUserPath = -not [string]::IsNullOrWhiteSpace($env:USERPROFILE) -and
            ($ascii.IndexOf($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
             $unicode.IndexOf($env:USERPROFILE, [StringComparison]::OrdinalIgnoreCase) -ge 0)
        if ($containsRepositoryPath -or $containsUserPath) {
            throw "Release binary contains a source-machine path: $relative"
        }
    }
}

$manifestFiles = @($payloadFiles | Sort-Object { Get-RelativePackagePath $payload $_.FullName } | ForEach-Object {
    [ordered]@{
        path = Get-RelativePackagePath $payload $_.FullName
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{
    schemaVersion = 'calibre-library-cleaner-release-manifest/1.0'
    product = 'CalibreLibraryCleaner'
    version = $Version
    runtimeIdentifier = $runtimeIdentifier
    deployment = $deployment
    files = $manifestFiles
}
[IO.File]::WriteAllText(
    $manifestPath,
    (($manifest | ConvertTo-Json -Depth 6) + "`n"),
    $utf8NoBom)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
$zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $fixedTimestamp = New-Object DateTimeOffset(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $filesToArchive = @(Get-ChildItem -LiteralPath $payload -File -Recurse |
            Sort-Object { Get-RelativePackagePath $payload $_.FullName })
        foreach ($file in $filesToArchive) {
            $relative = Get-RelativePackagePath $payload $file.FullName
            $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTimestamp
            $entryStream = $entry.Open()
            $input = [IO.File]::OpenRead($file.FullName)
            try {
                $input.CopyTo($entryStream)
            }
            finally {
                $input.Dispose()
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $zipStream.Dispose()
}

$zip = Get-Item -LiteralPath $zipPath
[pscustomobject]@{
    Package = $zip.FullName
    Size = $zip.Length
    Sha256 = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Files = $manifestFiles.Count
} | Format-List