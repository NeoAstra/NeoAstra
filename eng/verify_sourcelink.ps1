[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SymbolPackage,

    [Parameter(Mandatory = $true)]
    [string] $Repository,

    [Parameter(Mandatory = $true)]
    [string] $Commit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "Repository must have the GitHub owner/name form: $Repository"
}
if ($Commit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Commit must be a full 40-character Git object ID: $Commit"
}
if (-not (Test-Path -LiteralPath $SymbolPackage -PathType Leaf)) {
    throw "Symbol package was not found: $SymbolPackage"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Reflection.Metadata

$sourceLinkKind = [Guid] 'cc110556-a091-4d38-9fec-25ab9a351a6a'
$expectedUrl = "https://raw.githubusercontent.com/$Repository/$($Commit.ToLowerInvariant())/*"
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $SymbolPackage))
try {
    $pdbEntries = @($archive.Entries | Where-Object FullName -CMatch '^lib/net10\.0/[^/]+\.pdb$')
    if ($pdbEntries.Count -ne 1) {
        throw "Expected exactly one managed PDB entry in $SymbolPackage; found $($pdbEntries.Count)"
    }
    $pdbEntryName = $pdbEntries[0].FullName

    $pdbStream = [IO.MemoryStream]::new()
    $entryStream = $pdbEntries[0].Open()
    try {
        $entryStream.CopyTo($pdbStream)
    }
    finally {
        $entryStream.Dispose()
    }
    $pdbStream.Position = 0

    $provider = [System.Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($pdbStream)
    try {
        $reader = $provider.GetMetadataReader()
        $sourceLinkDocuments = @(
            foreach ($handle in $reader.CustomDebugInformation) {
                $information = $reader.GetCustomDebugInformation($handle)
                if ($information.Parent.Kind -eq [System.Reflection.Metadata.HandleKind]::ModuleDefinition -and
                    $reader.GetGuid($information.Kind) -eq $sourceLinkKind) {
                    [Text.Encoding]::UTF8.GetString($reader.GetBlobBytes($information.Value))
                }
            }
        )
    }
    finally {
        $provider.Dispose()
    }
}
finally {
    $archive.Dispose()
}

if ($sourceLinkDocuments.Count -ne 1) {
    throw "Expected exactly one Source Link record in $pdbEntryName; found $($sourceLinkDocuments.Count)"
}

try {
    $sourceLink = $sourceLinkDocuments[0] | ConvertFrom-Json
    $mappings = @($sourceLink.documents.PSObject.Properties)
}
catch {
    throw "Source Link record is not valid JSON: $($_.Exception.Message)"
}

$matchingMappings = @($mappings | Where-Object { $_.Value -ceq $expectedUrl })
if ($mappings.Count -ne 1 -or $matchingMappings.Count -ne 1) {
    $actualMappings = $mappings | ForEach-Object { "$($_.Name) -> $($_.Value)" }
    throw "Source Link must contain exactly one mapping to the expected commit-pinned repository URL $expectedUrl. Actual mappings: $($actualMappings -join '; ')"
}

Write-Host "Verified $pdbEntryName Source Link mapping: $($matchingMappings[0].Name) -> $expectedUrl"
