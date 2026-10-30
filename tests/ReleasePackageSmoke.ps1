param([string]$Version = '1.0.3')

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-Check($condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Read-ZipText($zip, [string]$name) {
    $reader = [IO.StreamReader]::new($zip.GetEntry($name).Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Test-Package([string]$modid, [string]$assembly, [string]$side, [hashtable]$files) {
    $package = Join-Path $projectRoot "artifacts/$modid-$Version.zip"
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $actual = @($zip.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') })
        $difference = @(Compare-Object @($files.Keys | Sort-Object) @($actual | Sort-Object))
        Assert-Check ($difference.Count -eq 0) "$modid package contains missing, duplicate or unexpected files: $($difference | Out-String)"
        foreach ($name in $files.Keys) {
            $entry = $zip.GetEntry($name)
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [Convert]::ToHexString($sha.ComputeHash($stream)) }
            finally { $stream.Dispose(); $sha.Dispose() }
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path $projectRoot $files[$name]) -Algorithm SHA256).Hash
            Assert-Check ($hash -eq $sourceHash) "$modid/$name differs from the current source or Release build"
        }
        $manifest = Read-ZipText $zip 'modinfo.json' | ConvertFrom-Json
        Assert-Check ($manifest.modid -ceq $modid -and $manifest.version -ceq $Version -and $manifest.side -ceq $side) "$modid has incorrect identity/version/side"
        $assemblyPath = Join-Path $projectRoot $files[$assembly]
        $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString()
        Assert-Check ($assemblyVersion -ceq "$Version.0") "$modid assembly version is $assemblyVersion"
        if ($side -eq 'Client') {
            Assert-Check ($manifest.dependencies.vsimgui -ceq '1.2.7') 'Client must require VSImGui 1.2.7'
            $en = Read-ZipText $zip 'assets/buildwright/lang/en.json' | ConvertFrom-Json -AsHashtable
            $zh = Read-ZipText $zip 'assets/buildwright/lang/zh-cn.json' | ConvertFrom-Json -AsHashtable
            $keyDifference = @(Compare-Object @($en.Keys | Sort-Object) @($zh.Keys | Sort-Object))
            Assert-Check ($keyDifference.Count -eq 0) 'Client language key sets differ'
            foreach ($language in @($en, $zh)) {
                Assert-Check (-not @($language.Keys | Where-Object { $_ -match 'website|ui\.schematics\.' }).Count) 'Removed website keys remain in the package'
                Assert-Check (-not [string]::IsNullOrWhiteSpace($language['buildwright:ui.model.material-rights'])) 'Model material-rights notice is missing'
            }
        }
        Write-Output "$modid $Version passed: $($actual.Count) files, assembly $assemblyVersion, source/ZIP SHA256 match."
    } finally { $zip.Dispose() }
}

Test-Package 'buildwright' 'Buildwright.dll' 'Client' @{
    'Buildwright.dll' = 'bin/Release/Mods/buildwright/Buildwright.dll'
    'modinfo.json' = 'modinfo.json'
    'COPYRIGHT.md' = 'COPYRIGHT.md'
    'LICENSE' = 'LICENSE'
    'LICENSE-Fieldwright.txt' = 'LICENSE-Fieldwright.txt'
    'assets/buildwright/lang/en.json' = 'assets/buildwright/lang/en.json'
    'assets/buildwright/lang/zh-cn.json' = 'assets/buildwright/lang/zh-cn.json'
}
Test-Package 'buildwrightserver' 'BuildwrightServer.dll' 'Server' @{
    'BuildwrightServer.dll' = 'bin/Release/Mods/buildwrightserver/BuildwrightServer.dll'
    'modinfo.json' = 'BuildwrightServer/modinfo.json'
    'COPYRIGHT.md' = 'BuildwrightServer/COPYRIGHT.md'
    'LICENSE' = 'LICENSE'
}
