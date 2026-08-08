[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $InputPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $SearchText,

    [string] $OutputPath,

    [string] $NewtonsoftJsonPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-NewtonsoftJsonAssembly {
    param([string] $RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = (Resolve-Path -LiteralPath $RequestedPath).Path
        return $resolved
    }

    $packageRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
        Join-Path $HOME '.nuget\packages'
    }
    else {
        $env:NUGET_PACKAGES
    }
    $packageDirectory = Join-Path $packageRoot 'newtonsoft.json'
    if (Test-Path -LiteralPath $packageDirectory -PathType Container) {
        $packageAssembly = Get-ChildItem -LiteralPath $packageDirectory -Filter 'Newtonsoft.Json.dll' -File -Recurse |
            Where-Object { $_.FullName -match '[\\/]lib[\\/]net45[\\/]' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $packageAssembly) {
            return $packageAssembly.FullName
        }
    }

    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $repositoryRoot 'src\CalibreLibraryCleaner.Infrastructure\bin\Debug\net10.0\Newtonsoft.Json.dll'),
        (Join-Path $repositoryRoot 'src\CalibreLibraryCleaner.Wpf\bin\Debug\net10.0-windows\Newtonsoft.Json.dll'),
        (Join-Path $repositoryRoot 'src\CalibreLibraryCleaner.Infrastructure\bin\Release\net10.0\Newtonsoft.Json.dll'),
        (Join-Path $repositoryRoot 'src\CalibreLibraryCleaner.Wpf\bin\Release\net10.0-windows\Newtonsoft.Json.dll')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Newtonsoft.Json.dll was not found. Build the solution first or pass -NewtonsoftJsonPath.'
}

$sourcePath = (Resolve-Path -LiteralPath $InputPath).Path
$sourceInfo = Get-Item -LiteralPath $sourcePath
if ($sourceInfo.Length -le 0) {
    throw 'The input snapshot is empty.'
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $safeSearchText = [System.Text.RegularExpressions.Regex]::Replace($SearchText, '[^A-Za-z0-9._-]+', '_').Trim('_')
    if ([string]::IsNullOrWhiteSpace($safeSearchText)) {
        $safeSearchText = 'matches'
    }
    $OutputPath = Join-Path $sourceInfo.DirectoryName ($sourceInfo.BaseName + ".${safeSearchText}.matches.json")
}

$destinationPath = [System.IO.Path]::GetFullPath($OutputPath)
if ([string]::Equals($sourcePath, $destinationPath, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output path must differ from the input snapshot path.'
}

$destinationDirectory = Split-Path -Parent $destinationPath
if ([string]::IsNullOrWhiteSpace($destinationDirectory)) {
    $destinationDirectory = (Get-Location).Path
    $destinationPath = Join-Path $destinationDirectory (Split-Path -Leaf $destinationPath)
}
[System.IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null

$assemblyPath = Resolve-NewtonsoftJsonAssembly $NewtonsoftJsonPath
Add-Type -Path $assemblyPath

$temporaryPath = Join-Path $destinationDirectory ('.' + [System.Guid]::NewGuid().ToString('N') + '.tmp')
$inputStream = $null
$inputReader = $null
$jsonReader = $null
$outputStream = $null
$outputTextWriter = $null
$jsonWriter = $null
$scannedCount = 0L
$matchCount = 0L
$collectionCounts = @{}

try {
    $inputStream = [System.IO.FileStream]::new(
        $sourcePath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read,
        1MB,
        [System.IO.FileOptions]::SequentialScan)
    $inputReader = [System.IO.StreamReader]::new(
        $inputStream,
        [System.Text.UTF8Encoding]::new($false, $true),
        $true,
        1MB,
        $true)
    $jsonReader = [Newtonsoft.Json.JsonTextReader]::new($inputReader)
    $jsonReader.DateParseHandling = [Newtonsoft.Json.DateParseHandling]::None
    $jsonReader.FloatParseHandling = [Newtonsoft.Json.FloatParseHandling]::Decimal
    $jsonReader.MaxDepth = 128

    $outputStream = [System.IO.FileStream]::new(
        $temporaryPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None,
        64KB,
        [System.IO.FileOptions]::SequentialScan)
    $outputTextWriter = [System.IO.StreamWriter]::new(
        $outputStream,
        [System.Text.UTF8Encoding]::new($false),
        64KB,
        $true)
    $jsonWriter = [Newtonsoft.Json.JsonTextWriter]::new($outputTextWriter)
    $jsonWriter.Formatting = [Newtonsoft.Json.Formatting]::Indented

    $jsonWriter.WriteStartObject()
    $jsonWriter.WritePropertyName('sourceFile')
    $jsonWriter.WriteValue($sourcePath)
    $jsonWriter.WritePropertyName('searchText')
    $jsonWriter.WriteValue($SearchText)
    $jsonWriter.WritePropertyName('matches')
    $jsonWriter.WriteStartArray()

    while ($jsonReader.Read()) {
        if ($jsonReader.TokenType -ne [Newtonsoft.Json.JsonToken]::StartArray) {
            continue
        }

        $arrayPath = $jsonReader.Path
        if ($arrayPath -notmatch '^snapshot\.([^\.\[\]]+)$') {
            continue
        }
        $collectionName = $Matches[1]

        while ($jsonReader.Read()) {
            if ($jsonReader.TokenType -eq [Newtonsoft.Json.JsonToken]::EndArray) {
                break
            }

            $item = [Newtonsoft.Json.Linq.JToken]::ReadFrom($jsonReader)
            $scannedCount++
            $compactJson = $item.ToString([Newtonsoft.Json.Formatting]::None)
            if ($compactJson.IndexOf($SearchText, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $jsonWriter.WriteStartObject()
                $jsonWriter.WritePropertyName('collection')
                $jsonWriter.WriteValue($collectionName)
                $jsonWriter.WritePropertyName('item')
                $item.WriteTo($jsonWriter)
                $jsonWriter.WriteEndObject()
                $matchCount++
                $currentCollectionCount = if ($collectionCounts.ContainsKey($collectionName)) {
                    [int] $collectionCounts[$collectionName]
                }
                else {
                    0
                }
                $collectionCounts[$collectionName] = 1 + $currentCollectionCount
            }

            if (($scannedCount % 1000) -eq 0) {
                $percent = [Math]::Min(100, [int](100 * $inputStream.Position / $sourceInfo.Length))
                Write-Progress -Activity 'Searching library snapshot' `
                    -Status "$scannedCount items scanned; $matchCount matches" `
                    -PercentComplete $percent
            }
        }
    }

    $jsonWriter.WriteEndArray()
    $jsonWriter.WritePropertyName('matchCount')
    $jsonWriter.WriteValue($matchCount)
    $jsonWriter.WritePropertyName('scannedItemCount')
    $jsonWriter.WriteValue($scannedCount)
    $jsonWriter.WritePropertyName('matchesByCollection')
    $jsonWriter.WriteStartObject()
    foreach ($collectionName in $collectionCounts.Keys | Sort-Object) {
        $jsonWriter.WritePropertyName($collectionName)
        $jsonWriter.WriteValue($collectionCounts[$collectionName])
    }
    $jsonWriter.WriteEndObject()
    $jsonWriter.WriteEndObject()
    $jsonWriter.Flush()
    $outputTextWriter.Flush()
    $outputStream.Flush($true)

    $jsonWriter.Close()
    $jsonWriter = $null
    $outputTextWriter.Dispose()
    $outputTextWriter = $null
    $outputStream.Dispose()
    $outputStream = $null

    if ([System.IO.File]::Exists($destinationPath)) {
        [System.IO.File]::Replace($temporaryPath, $destinationPath, $null)
    }
    else {
        [System.IO.File]::Move($temporaryPath, $destinationPath)
    }
    Write-Progress -Activity 'Searching library snapshot' -Completed
    [pscustomobject]@{
        InputPath = $sourcePath
        OutputPath = $destinationPath
        SearchText = $SearchText
        ScannedItemCount = $scannedCount
        MatchCount = $matchCount
        MatchesByCollection = $collectionCounts
    }
}
finally {
    if ($null -ne $jsonWriter) { $jsonWriter.Close() }
    if ($null -ne $outputTextWriter) { $outputTextWriter.Dispose() }
    if ($null -ne $outputStream) { $outputStream.Dispose() }
    if ($null -ne $jsonReader) { $jsonReader.Close() }
    if ($null -ne $inputReader) { $inputReader.Dispose() }
    if ($null -ne $inputStream) { $inputStream.Dispose() }
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    Write-Progress -Activity 'Searching library snapshot' -Completed
}
