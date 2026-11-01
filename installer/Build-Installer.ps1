param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PublishPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$Compiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Compiler)) { throw "Inno Setup compiler not found: $Compiler" }

# Windows Desktop Runtime ZIP contains only the WindowsDesktop.App layer; combine it with the core runtime ZIP.
$metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json'
$release = $metadata.releases | Where-Object { $_.'windowsdesktop'.version -eq $metadata.'latest-runtime' } | Select-Object -First 1
$desktopRuntime = $release.windowsdesktop.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
$coreRuntime = $release.runtime.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (-not $desktopRuntime -or -not $coreRuntime -or
    $desktopRuntime.url -notlike 'https://builds.dotnet.microsoft.com/*' -or
    $coreRuntime.url -notlike 'https://builds.dotnet.microsoft.com/*') {
    throw 'Microsoft release metadata did not contain the expected Windows x64 .NET and Windows Desktop Runtime archives.'
}
$desktopRuntimeFile = Join-Path $OutputPath 'desktop-runtime-validation.zip'
$coreRuntimeFile = Join-Path $OutputPath 'core-runtime-validation.zip'
$validationPath = Join-Path $OutputPath 'runtime-validation'
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
try {
    Invoke-WebRequest $coreRuntime.url -OutFile $coreRuntimeFile
    Invoke-WebRequest $desktopRuntime.url -OutFile $desktopRuntimeFile
    if ((Get-FileHash $coreRuntimeFile -Algorithm SHA512).Hash -ne $coreRuntime.hash -or
        (Get-FileHash $desktopRuntimeFile -Algorithm SHA512).Hash -ne $desktopRuntime.hash) {
        throw 'A downloaded runtime archive does not match the Microsoft release metadata SHA-512 digest.'
    }
    Expand-Archive -LiteralPath $coreRuntimeFile -DestinationPath $validationPath -Force
    Expand-Archive -LiteralPath $desktopRuntimeFile -DestinationPath $validationPath -Force
    if (-not (Test-Path -LiteralPath (Join-Path $validationPath 'dotnet.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $validationPath 'shared\Microsoft.WindowsDesktop.App')) -or
        -not (Test-Path -LiteralPath (Join-Path $validationPath 'shared\Microsoft.NETCore.App'))) {
        throw 'The runtime archive does not contain the expected local .NET Desktop Runtime files.'
    }

    $coreSHA256 = (Get-FileHash $coreRuntimeFile -Algorithm SHA256).Hash
    $desktopSHA256 = (Get-FileHash $desktopRuntimeFile -Algorithm SHA256).Hash
    & $Compiler "/DAppVersion=$Version" "/DPublishPath=$PublishPath" "/DOutputPath=$OutputPath" `
        "/DCoreRuntimeUrl=$($coreRuntime.url)" "/DCoreRuntimeSHA256=$coreSHA256" `
        "/DDesktopRuntimeUrl=$($desktopRuntime.url)" "/DDesktopRuntimeSHA256=$desktopSHA256" `
        "$PSScriptRoot\Tracker.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed (exit code $LASTEXITCODE)." }
} finally {
    if (Test-Path -LiteralPath $coreRuntimeFile) { Remove-Item -LiteralPath $coreRuntimeFile -Force }
    if (Test-Path -LiteralPath $desktopRuntimeFile) { Remove-Item -LiteralPath $desktopRuntimeFile -Force }
    if (Test-Path -LiteralPath $validationPath) { Remove-Item -LiteralPath $validationPath -Recurse -Force }
}
