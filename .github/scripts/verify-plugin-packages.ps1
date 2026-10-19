$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$packageRoot = Join-Path $repositoryRoot 'PluginPackages'
$pluginIds = @(
    'nativetavern.dynamic-character-status'
    'nativetavern.story-prompts'
    'nativetavern.dice-random-tables'
    'nativetavern.story-timeline'
)

foreach ($pluginId in $pluginIds) {
    $sourceDirectory = Join-Path $packageRoot $pluginId
    $archivePath = Join-Path $packageRoot "$pluginId.ntplugin"
    if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
        throw "Missing plugin source directory: $sourceDirectory"
    }
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "Missing plugin package: $archivePath"
    }

    $expected = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sourceFile in @(Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File -Force)) {
        $relativePath = [IO.Path]::GetRelativePath($sourceDirectory, $sourceFile.FullName).Replace('\', '/')
        [void]$expected.Add($relativePath)
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $archiveFiles = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        if ($archiveFiles.Count -ne $expected.Count) {
            throw "Plugin package file count differs from source: $pluginId"
        }
        foreach ($entry in $archiveFiles) {
            $entryName = $entry.FullName.Replace('\', '/')
            if (-not $expected.Remove($entryName)) {
                throw "Unexpected or duplicate file in plugin package $pluginId`: $entryName"
            }
            $sourcePath = Join-Path $sourceDirectory $entryName.Replace('/', [IO.Path]::DirectorySeparatorChar)
            $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
            $entryStream = $entry.Open()
            try {
                $archiveHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream))
            }
            finally { $entryStream.Dispose() }
            if ($archiveHash -ne $sourceHash) {
                throw "Plugin package content differs from source: $pluginId/$entryName"
            }
        }
        if ($expected.Count -ne 0) {
            throw "Plugin package is missing source files: $pluginId"
        }
    }
    finally { $archive.Dispose() }

    Write-Host "Verified plugin package: $pluginId"
}
