[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ZipPath = [IO.Path]::GetFullPath($ZipPath)
if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
    throw "Release ZIP does not exist: $ZipPath"
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("CalibreLibraryCleaner-release-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
        $uniqueEntryCount = @($entryNames | Sort-Object -Unique).Count
        $uniqueCaseInsensitiveCount = @($entryNames |
            ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique).Count
        if ($entryNames.Count -eq 0 -or
            $entryNames.Count -ne $uniqueEntryCount -or
            $entryNames.Count -ne $uniqueCaseInsensitiveCount) {
            throw 'Release ZIP entries are empty or duplicated.'
        }
        foreach ($name in $entryNames) {
            if ($name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/') -or
                $name -match '(^|/)\.\.(/|$)') {
                throw "Unsafe ZIP entry: $name"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
    [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $temporaryRoot)

    $manifestPath = Join-Path $temporaryRoot 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 'calibre-library-cleaner-release-manifest/1.0' -or
        $manifest.runtimeIdentifier -ne 'win-x64' -or
        $manifest.deployment -ne 'framework-dependent') {
        throw 'Release manifest identity is invalid.'
    }

    $actualFiles = @(Get-ChildItem -LiteralPath $temporaryRoot -File -Recurse |
        Where-Object { $_.FullName -ne $manifestPath } |
        ForEach-Object {
            [pscustomobject]@{
                path = $_.FullName.Substring($temporaryRoot.Length + 1).Replace('\', '/')
                size = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        } | Sort-Object path)
    $expectedFiles = @($manifest.files | Sort-Object path)
    if ($actualFiles.Count -ne $expectedFiles.Count) {
        throw 'Release manifest file count does not match the ZIP.'
    }
    for ($index = 0; $index -lt $actualFiles.Count; $index++) {
        if ($actualFiles[$index].path -cne $expectedFiles[$index].path -or
            $actualFiles[$index].size -ne $expectedFiles[$index].size -or
            $actualFiles[$index].sha256 -cne $expectedFiles[$index].sha256) {
            throw "Release manifest mismatch: $($actualFiles[$index].path)"
        }
    }

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
        if (-not ($actualFiles.path -ccontains $relative)) {
            throw "Required release file is missing: $relative"
        }
    }
    $forbiddenName = '(?i)(\.pdb$|\.log$|\.tmp$|\.cache(?:[-./]|$)|\.user$|\.suo$|testhost|(^|/)(?:test|tests|cache|caches|log|logs|credential|credentials)(/|$)|\.tests?\.|api.?key|library-state|library-snapshot|metadata-cover-staging)'
    $forbidden = @($actualFiles.path | Where-Object { $_ -match $forbiddenName })
    if ($forbidden.Count -gt 0) {
        throw "Forbidden release files: $($forbidden -join ', ')"
    }
    foreach ($binary in @($actualFiles | Where-Object { [IO.Path]::GetExtension($_.path) -in @('.exe', '.dll') })) {
        $stream = [IO.File]::OpenRead((Join-Path $temporaryRoot $binary.path.Replace('/', '\')))
        try {
            if ($stream.Length -lt 64 -or $stream.ReadByte() -ne 0x4d -or $stream.ReadByte() -ne 0x5a) {
                throw "Release binary is not a valid PE image: $($binary.path)"
            }
        }
        finally {
            $stream.Dispose()
        }
    }

    $wpfRuntimeConfig = Get-Content -LiteralPath (Join-Path $temporaryRoot 'CalibreLibraryCleaner.Wpf.runtimeconfig.json') -Raw |
        ConvertFrom-Json
    $desktopFramework = @($wpfRuntimeConfig.runtimeOptions.frameworks | Where-Object {
        $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -like '10.*'
    })
    if ($wpfRuntimeConfig.runtimeOptions.tfm -ne 'net10.0' -or $desktopFramework.Count -ne 1) {
        throw 'The package does not declare the .NET 10 Windows Desktop Runtime.'
    }
    $pdfRuntimeConfig = Get-Content -LiteralPath (
        Join-Path $temporaryRoot 'pdf-worker\CalibreLibraryCleaner.PdfWorker.runtimeconfig.json') -Raw |
        ConvertFrom-Json
    if ($pdfRuntimeConfig.runtimeOptions.tfm -ne 'net10.0' -or
        $pdfRuntimeConfig.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App' -or
        $pdfRuntimeConfig.runtimeOptions.framework.version -notlike '10.*') {
        throw 'The PDF worker does not declare the .NET 10 runtime.'
    }
    foreach ($dependencyFile in @(
        @{ Path = 'CalibreLibraryCleaner.Wpf.deps.json'; Product = 'CalibreLibraryCleaner.Wpf'; Runtime = 'CalibreLibraryCleaner.Wpf.dll' },
        @{ Path = 'pdf-worker\CalibreLibraryCleaner.PdfWorker.deps.json'; Product = 'CalibreLibraryCleaner.PdfWorker'; Runtime = 'CalibreLibraryCleaner.PdfWorker.dll' }
    )) {
        $dependencies = Get-Content -LiteralPath (Join-Path $temporaryRoot $dependencyFile.Path) -Raw |
            ConvertFrom-Json
        $runtimeTarget = '.NETCoreApp,Version=v10.0/win-x64'
        $target = $dependencies.targets.PSObject.Properties[$runtimeTarget].Value
        $product = @($target.PSObject.Properties | Where-Object {
            $_.Name.StartsWith($dependencyFile.Product + '/', [StringComparison]::Ordinal)
        })
        if ($dependencies.runtimeTarget.name -ne $runtimeTarget -or $product.Count -ne 1 -or
            $null -eq $product[0].Value.runtime.PSObject.Properties[$dependencyFile.Runtime]) {
            throw "Dependency graph is invalid: $($dependencyFile.Path)"
        }
    }

    $workerProcessesBefore = @(Get-Process -Name 'CalibreLibraryCleaner.PdfWorker' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Id)
    $executable = Join-Path $temporaryRoot 'CalibreLibraryCleaner.Wpf.exe'
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $executable
    $startInfo.Arguments = '--release-smoke'
    $startInfo.WorkingDirectory = $temporaryRoot
    $startInfo.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($startInfo)
    if (-not $process.WaitForExit(15000)) {
        $process.Kill()
        throw 'Packaged executable launch smoke timed out.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Packaged executable launch smoke failed with exit code $($process.ExitCode)."
    }
    $workerProcessesAfter = @(Get-Process -Name 'CalibreLibraryCleaner.PdfWorker' -ErrorAction SilentlyContinue |
        Where-Object { $workerProcessesBefore -notcontains $_.Id })
    if ($workerProcessesAfter.Count -gt 0) {
        $workerProcessesAfter | Stop-Process -Force -ErrorAction SilentlyContinue
        throw 'Packaged executable smoke unexpectedly started a PDF worker.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

[pscustomobject]@{
    Package = $ZipPath
    Sha256 = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Smoke = 'passed'
} | Format-List