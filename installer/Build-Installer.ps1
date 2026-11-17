[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$IsccPath,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.1.0'
)

$ErrorActionPreference = 'Stop'
$published = (Resolve-Path -LiteralPath $PublishDir).ProviderPath
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
$publishPrefix = $published.TrimEnd([char]'\') + '\'
$outputPrefix = $output.TrimEnd([char]'\') + '\'
if ($outputPrefix.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDir must be outside PublishDir so a previous setup cannot be included as payload.'
}

foreach ($required in @('Backthread.exe', 'Assets\Backthread.ico', 'Backthread.pri', 'App.xbf',
        'MainWindow.xbf', 'Microsoft.UI.Xaml.dll', 'coreclr.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $required) -PathType Leaf)) {
        throw "Missing $required. Supply the self-contained win-x64 dotnet publish directory."
    }
}

if (-not $IsccPath) { $IsccPath = $env:INNO_ISCC }
if (-not $IsccPath) {
    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $IsccPath = $command.Source }
}
if (-not $IsccPath) {
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))
    foreach ($root in $roots) {
        if (-not $root) { continue }
        foreach ($name in @('Inno Setup 6', 'Inno Setup 7', 'Inno Setup 7-x64')) {
            $candidate = Join-Path (Join-Path $root $name) 'ISCC.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $IsccPath = $candidate
                break
            }
        }
        if ($IsccPath) { break }
    }
}
if (-not $IsccPath) { throw 'Inno Setup compiler not found. Supply -IsccPath or set INNO_ISCC.' }
$compiler = (Resolve-Path -LiteralPath $IsccPath).ProviderPath
New-Item -ItemType Directory -Path $output -Force | Out-Null

$script = Join-Path $PSScriptRoot 'Backthread.iss'
& $compiler "/DPublishDir=$published" "/DBuildVersion=$Version" "/O$output" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$file = Join-Path $output "Backthread-$Version-win-x64-Setup.exe"
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "ISCC did not produce $file." }
$sha = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
$name = [IO.Path]::GetFileName($file)
[IO.File]::WriteAllText("$file.sha256", "$sha  $name`n", [Text.Encoding]::ASCII)
Write-Output $file
