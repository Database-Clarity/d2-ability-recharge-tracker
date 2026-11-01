param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PublishPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$Compiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Compiler)) { throw "Inno Setup compiler not found: $Compiler" }

# Pin the runtime URL and digest into each installer, without bundling its bytes.
$metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json'
$release = $metadata.releases | Where-Object { $_.'windowsdesktop'.version -eq $metadata.'latest-runtime' } | Select-Object -First 1
$runtime = $release.windowsdesktop.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.exe' } | Select-Object -First 1
if (-not $runtime -or $runtime.url -notlike 'https://builds.dotnet.microsoft.com/*') {
    throw 'Microsoft release metadata did not contain the expected Windows Desktop Runtime installer.'
}
$runtimeFile = Join-Path $OutputPath 'runtime-validation.exe'
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
try {
    Invoke-WebRequest $runtime.url -OutFile $runtimeFile
    if ((Get-FileHash $runtimeFile -Algorithm SHA512).Hash -ne $runtime.hash) {
        throw 'The downloaded runtime does not match the Microsoft release metadata SHA-512 digest.'
    }
    $sha256 = (Get-FileHash $runtimeFile -Algorithm SHA256).Hash
    & $Compiler "/DAppVersion=$Version" "/DPublishPath=$PublishPath" "/DOutputPath=$OutputPath" "/DRuntimeUrl=$($runtime.url)" "/DRuntimeSHA256=$sha256" "$PSScriptRoot\Tracker.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed (exit code $LASTEXITCODE)." }
} finally {
    if (Test-Path -LiteralPath $runtimeFile) { Remove-Item -LiteralPath $runtimeFile -Force }
}
