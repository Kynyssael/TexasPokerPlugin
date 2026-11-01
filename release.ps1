# Builds the plugin in Release and updates the Dalamud custom repository files:
#   release/latest.zip   the plugin package Dalamud downloads
#   repo.json            the repository listing players add in /xlsettings
# Bump <Version> in TexasPoker/TexasPoker.csproj first, run this, then commit and push.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$rawBase = 'https://raw.githubusercontent.com/Kynyssael/TexasPokerPlugin/main'

dotnet build "$root\TexasPoker\TexasPoker.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$zip = "$root\TexasPoker\bin\Release\TexasPoker\latest.zip"
New-Item -ItemType Directory -Force "$root\release" | Out-Null
Copy-Item $zip "$root\release\latest.zip" -Force

# Take the manifest DalamudPackager wrote into the zip, so repo.json always matches the package.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entry = $archive.Entries | Where-Object Name -eq 'TexasPoker.json'
    $reader = New-Object IO.StreamReader($entry.Open())
    $manifest = $reader.ReadToEnd() | ConvertFrom-Json
    $reader.Close()
} finally {
    $archive.Dispose()
}

$download = "$rawBase/release/latest.zip"
$listing = [ordered]@{
    Author              = $manifest.Author
    Name                = $manifest.Name
    InternalName        = $manifest.InternalName
    AssemblyVersion     = $manifest.AssemblyVersion
    Punchline           = $manifest.Punchline
    Description         = $manifest.Description
    ApplicableVersion   = $manifest.ApplicableVersion
    RepoUrl             = $manifest.RepoUrl
    Tags                = @($manifest.Tags)
    DalamudApiLevel     = $manifest.DalamudApiLevel
    LoadPriority        = $manifest.LoadPriority
    AcceptsFeedback     = $manifest.AcceptsFeedback
    IsHide              = $false
    IsTestingExclusive  = $false
    DownloadCount       = 0
    LastUpdate          = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    DownloadLinkInstall = $download
    DownloadLinkUpdate  = $download
    DownloadLinkTesting = $download
}

# No BOM: write plain UTF-8.
$json = ConvertTo-Json -InputObject @($listing) -Depth 5
[IO.File]::WriteAllText("$root\repo.json", $json + "`n", (New-Object Text.UTF8Encoding($false)))

Write-Host "Packaged $($manifest.Name) $($manifest.AssemblyVersion) (API $($manifest.DalamudApiLevel))."
Write-Host "Commit release/latest.zip and repo.json, then push."
