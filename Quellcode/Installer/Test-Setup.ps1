param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][version]$Version
)

$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Dieser Test ersetzt die installierte App auf einem frischen GitHub-Runner.'
}

$setupPath = (Get-Item -LiteralPath $Setup).FullName
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setupPath)
if ($info.ProductName -ne 'WebUntis-FehlzeitenMX' -or $info.ProductMajorPart -ne $Version.Major -or
    $info.ProductMinorPart -ne $Version.Minor -or $info.ProductBuildPart -ne $Version.Build -or $info.ProductPrivatePart -ne 0) {
    throw 'Produktname oder Version des Setups stimmen nicht.'
}

function Install-Setup {
    $process = Start-Process -FilePath $setupPath -ArgumentList '/S' -PassThru
    if (!$process.WaitForExit(90000)) { throw 'Das Setup hat die Installation nicht beendet.' }
    if ($process.ExitCode -ne 0) { throw "Das Setup meldet Fehler $($process.ExitCode)." }
}

Install-Setup
$installed = Join-Path $env:LOCALAPPDATA 'Programs/WebUntis-Auswertung/Programm/WebUntis-Auswertung.exe'
if (!(Test-Path -LiteralPath $installed)) { throw 'Die Programmdatei wurde nicht installiert.' }
$installedHash = (Get-FileHash -LiteralPath $installed).Hash

# Simulate the installed version with its normal window title and installation mutex.
$source = Join-Path $env:RUNNER_TEMP 'OpenApp.cs'
$fixture = Join-Path $env:RUNNER_TEMP 'OpenApp.exe'
@'
using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

static class OpenApp
{
    [STAThread]
    static void Main(string[] args)
    {
        using (var mutex = new Mutex(false, @"Local\WebUntisAuswertung-Installation"))
        {
            if (!mutex.WaitOne(0)) return;
            try
            {
                using (var form = new Form { Text = "WebUntis-FehlzeitenMX 2.6" })
                {
                    form.Shown += (_, __) => File.WriteAllText(args[0], "ready");
                    form.FormClosed += (_, __) => File.WriteAllText(args[0], "closed");
                    Application.Run(form);
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
'@ | Set-Content -LiteralPath $source
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:winexe /r:System.Windows.Forms.dll "/out:$fixture" $source
if ($LASTEXITCODE -ne 0) { throw 'Das Testfenster konnte nicht gebaut werden.' }
Copy-Item -LiteralPath $fixture -Destination $installed -Force
$marker = Join-Path $env:RUNNER_TEMP 'open-app-state'
$app = Start-Process -FilePath $installed -ArgumentList ('"{0}"' -f $marker) -PassThru
try {
    $waitUntil = [DateTime]::UtcNow.AddSeconds(10)
    while (!(Test-Path -LiteralPath $marker) -and !$app.HasExited -and [DateTime]::UtcNow -lt $waitUntil) {
        Start-Sleep -Milliseconds 100
    }
    if (!(Test-Path -LiteralPath $marker)) { throw 'Das Testfenster wurde nicht geöffnet.' }
    Install-Setup
    if (!$app.WaitForExit(1000) -or (Get-Content -LiteralPath $marker -Raw) -ne 'closed') {
        throw 'Das Setup hat die laufende App nicht regulär geschlossen.'
    }
    if ((Get-FileHash -LiteralPath $installed).Hash -ne $installedHash) {
        throw 'Das Setup hat die Programmdatei nicht ersetzt.'
    }
}
finally {
    if (!$app.HasExited) { Stop-Process -Id $app.Id }
}
