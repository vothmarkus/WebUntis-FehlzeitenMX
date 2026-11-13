[CmdletBinding(DefaultParameterSetName = 'Unsigned')]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'Ausgabe'),
    [string]$MakeNsis = 'makensis.exe',
    [string]$SignTool = 'signtool.exe',
    [Parameter(Mandatory, ParameterSetName = 'Certificate')]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$CertificateThumbprint,
    [Parameter(Mandatory, ParameterSetName = 'ArtifactSigning')]
    [string]$ArtifactSigningDll,
    [Parameter(Mandatory, ParameterSetName = 'ArtifactSigning')]
    [string]$ArtifactSigningMetadata,
    [string]$TimestampUrl = 'http://timestamp.acs.microsoft.com'
)

$ErrorActionPreference = 'Stop'

if ($MakeNsis -eq 'makensis.exe' -and -not (Get-Command $MakeNsis -ErrorAction SilentlyContinue)) {
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $MakeNsis = Join-Path $programFiles 'NSIS\makensis.exe'
}
$MakeNsis = (Get-Command $MakeNsis -CommandType Application).Source
$sign = $PSBoundParameters.ContainsKey('CertificateThumbprint') -or $PSBoundParameters.ContainsKey('ArtifactSigningDll')
if ($sign) {
    $SignTool = (Get-Command $SignTool -CommandType Application).Source
    $signOptions = @('/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($PSCmdlet.ParameterSetName -eq 'Certificate') {
        $signOptions += @('/sha1', $CertificateThumbprint)
    }
    else {
        $dlib = (Get-Item -LiteralPath $ArtifactSigningDll).FullName
        $metadata = (Get-Item -LiteralPath $ArtifactSigningMetadata).FullName
        $signOptions += @('/dlib', $dlib, '/dmdf', $metadata)
    }
}

function Sign-ReleaseFile([string]$Path) {
    & $SignTool sign @signOptions $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $Path." }
    & $SignTool verify /pa /tw $Path
    if ($LASTEXITCODE -ne 0) { throw "Signature or timestamp verification failed for $Path." }
}

$source = Split-Path -Parent $PSScriptRoot
$releaseRoot = Split-Path -Parent $source
$project = Join-Path $source 'Windows\WebUntisAuswertung.csproj'
[xml]$definition = Get-Content -LiteralPath $project
$appName = [string]$definition.Project.PropertyGroup.Product
$version = [string]$definition.Project.PropertyGroup.Version
$release = $version -replace '\.0$'
$output = New-Item -Path $OutputDirectory -ItemType Directory -Force
$installer = Join-Path $output.FullName "$appName-$release-Setup-x64.exe"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("WebUntis-Setup-" + [guid]::NewGuid().ToString('N'))
New-Item -Path $temp -ItemType Directory | Out-Null

try {
    $payload = Join-Path $temp 'Programm'
    $builtInstaller = Join-Path $temp (Split-Path -Leaf $installer)
    $publishOptions = @('-p:DebugType=None', '-p:DebugSymbols=false', '-p:SatelliteResourceLanguages=de')
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $payload @publishOptions
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    Get-ChildItem -LiteralPath $payload -Filter '*.xml' -File | Remove-Item
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'README.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'Start-hier.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'Lizenzen') -Destination $payload -Recurse
    if ($sign) {
        $assembly = [string]$definition.Project.PropertyGroup.AssemblyName
        foreach ($name in "$assembly.exe", "$assembly.dll", 'WebUntisAuswertung.Core.dll') {
            Sign-ReleaseFile (Join-Path $payload $name)
        }
    }

    $bootstrapper = Join-Path $temp 'MicrosoftEdgeWebview2Setup.exe'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
    $signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation(,|$)') {
        throw 'The WebView2 bootstrapper has no valid Microsoft signature.'
    }

    $setupSource = Join-Path $PSScriptRoot "$appName.nsi"
    $setupOptions = @('/V3', "/DAPP=$appName", "/DVERSION=$version", "/DOUTPUT=$builtInstaller", "/DPAYLOAD=$payload", "/DWEBVIEW_BOOTSTRAPPER=$bootstrapper")
    & $MakeNsis @setupOptions $setupSource
    if ($LASTEXITCODE -ne 0) { throw 'NSIS compilation failed.' }

    if ($sign) { Sign-ReleaseFile $builtInstaller }
    Move-Item -LiteralPath $builtInstaller -Destination $installer -Force
    Get-Item -LiteralPath $installer
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
