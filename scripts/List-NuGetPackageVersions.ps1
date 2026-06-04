# Scan all .csproj files and aggregate unique package versions
$packages = Get-ChildItem -Filter *.csproj -Recurse |
    Get-Content |
    Select-String -Pattern '<PackageReference Include="([^"]+)" Version="([^"]+)"' -AllMatches |
    ForEach-Object { $_.Matches } |
    Group-Object { $_.Groups[1].Value } |
    ForEach-Object { @{
        Name = $_.Name
        Versions = $_.Group.ForEach({ $_.Groups[2].Value }) | Select-Object -Unique
    }} |
    Sort-Object { $_.Name }

# Display results
$packages | ForEach-Object {
    "$($_.Name) versions:"
    $_.Versions | ForEach-Object { "  $_" }
}