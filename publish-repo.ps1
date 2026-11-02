# Publishes the Release build to the custom Dalamud plugin repository in repo/ (served from GitHub):
#   repo/pluginmaster.json   the link dealers add in Dalamud (Settings -> Experimental -> Custom Plugin Repositories)
#   repo/latest.zip          the plugin itself
# Run after a Release build, then commit and push repo/:   powershell -File publish-repo.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$raw = "https://raw.githubusercontent.com/Kynyssael/TexasPokerPlugin/main"
$zip = Join-Path $PSScriptRoot "TexasPoker\bin\Release\TexasPoker\latest.zip"
$repo = Join-Path $PSScriptRoot "repo"
if (-not (Test-Path $zip)) { throw "No Release build at $zip. Run: dotnet build TexasPoker.slnx -c Release" }

# The manifest DalamudPackager put in the zip has the version and API level of this build.
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $reader = New-Object System.IO.StreamReader(($archive.GetEntry("TexasPoker.json")).Open())
    $manifest = $reader.ReadToEnd() | ConvertFrom-Json
    $reader.Close()
} finally {
    $archive.Dispose()
}

New-Item -ItemType Directory -Force $repo | Out-Null
Copy-Item $zip (Join-Path $repo "latest.zip") -Force

$entry = [ordered]@{
    Author              = $manifest.Author
    Name                = $manifest.Name
    InternalName        = $manifest.InternalName
    AssemblyVersion     = $manifest.AssemblyVersion
    Punchline           = $manifest.Punchline
    Description         = $manifest.Description
    ApplicableVersion   = $manifest.ApplicableVersion
    Tags                = $manifest.Tags
    DalamudApiLevel     = $manifest.DalamudApiLevel
    LoadPriority        = $manifest.LoadPriority
    RepoUrl             = "https://github.com/Kynyssael/TexasPokerPlugin"
    IconUrl             = "$raw/TexasPoker/images/icon.png"
    DownloadLinkInstall = "$raw/repo/latest.zip"
    DownloadLinkUpdate  = "$raw/repo/latest.zip"
    DownloadLinkTesting = "$raw/repo/latest.zip"
    AcceptsFeedback     = $false
    IsHide              = $false
    IsTestingExclusive  = $false
    LastUpdate          = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
}

# Dalamud reads a JSON array of plugins; write it as UTF-8 without a BOM.
$json = ConvertTo-Json -InputObject @($entry) -Depth 4
[System.IO.File]::WriteAllText((Join-Path $repo "pluginmaster.json"), $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Published $($manifest.InternalName) $($manifest.AssemblyVersion) to repo/. Commit and push repo/ to release it."
