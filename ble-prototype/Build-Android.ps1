param(
    [string]$AndroidSdk = $env:ANDROID_HOME,
    [string]$JavaHome = $env:JAVA_HOME
)

$ErrorActionPreference = 'Stop'
if (!$AndroidSdk) { $AndroidSdk = $env:ANDROID_SDK_ROOT }
if (!$AndroidSdk -or !$JavaHome) {
    throw 'Set ANDROID_HOME and JAVA_HOME, or pass -AndroidSdk and -JavaHome.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

function Invoke-Tool([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program exited with $LASTEXITCODE" }
}

$androidJar = Join-Path $AndroidSdk 'platforms/android-36/android.jar'
$tools = Join-Path $AndroidSdk 'build-tools/36.0.0'
$out = Join-Path $PSScriptRoot 'out'
$work = Join-Path $out ('work/' + [Guid]::NewGuid().ToString('N'))
$classes = Join-Path $work 'classes'
$manifest = Join-Path $PSScriptRoot 'AndroidManifest.xml'
$final = Join-Path $out 'PhoneMediaBLE-0.1.5.apk'
$keystore = Join-Path $out 'local-debug.jks'
$password = 'android'

New-Item -ItemType Directory -Path $classes -Force | Out-Null
$java = Join-Path $JavaHome 'bin/java.exe'
$javaTemp = '-Djava.io.tmpdir=' + $work
$aapt = Join-Path $tools 'aapt2.exe'
$unsigned = Join-Path $work 'unsigned.apk'
$res = Join-Path $work 'res.zip'
$aligned = Join-Path $work 'aligned.apk'
$signed = Join-Path $work 'signed.apk'

try {
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'android') -Filter '*.java' -File | ForEach-Object FullName)
    Invoke-Tool (Join-Path $JavaHome 'bin/javac.exe') (@('-encoding', 'UTF-8', '--release', '17', '-classpath', $androidJar, '-d', $classes) + $sources)
    $classFiles = @(Get-ChildItem -LiteralPath $classes -Filter '*.class' -File -Recurse | ForEach-Object FullName)
    Invoke-Tool $java (@($javaTemp, '-cp', (Join-Path $tools 'lib/d8.jar'), 'com.android.tools.r8.D8',
        '--lib', $androidJar, '--min-api', '33', '--output', $work) + $classFiles)
    Invoke-Tool $aapt @('compile', '--dir', (Join-Path $PSScriptRoot 'res'), '-o', $res)
    Invoke-Tool $aapt @('link', '-I', $androidJar, '--manifest', $manifest, '--min-sdk-version', '33', '--target-sdk-version', '36', '-o', $unsigned, $res)
    $zip = [IO.Compression.ZipFile]::Open($unsigned, [IO.Compression.ZipArchiveMode]::Update)
    try {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $work 'classes.dex'), 'classes.dex') | Out-Null
    } finally {
        $zip.Dispose()
    }
    Invoke-Tool (Join-Path $tools 'zipalign.exe') @('-f', '4', $unsigned, $aligned)
    if (!(Test-Path -LiteralPath $keystore)) {
        Invoke-Tool (Join-Path $JavaHome 'bin/keytool.exe') @('-genkeypair', '-keystore', $keystore,
            '-storepass', $password, '-keypass', $password, '-alias', 'phone-media-ble-debug',
            '-dname', 'CN=Local Phone Media BLE Debug', '-keyalg', 'RSA', '-keysize', '2048', '-validity', '10000')
    }
    $signerJar = Join-Path $tools 'lib/apksigner.jar'
    Invoke-Tool $java @($javaTemp, '-jar', $signerJar, 'sign', '--ks', $keystore,
        '--ks-key-alias', 'phone-media-ble-debug', '--ks-pass', ('pass:' + $password), '--out', $signed, $aligned)
    Invoke-Tool $java @($javaTemp, '-jar', $signerJar, 'verify', '--verbose', $signed)
    Copy-Item -LiteralPath $signed -Destination $final -Force
    Get-Item -LiteralPath $final
} finally {
    $resolvedWork = (Resolve-Path -LiteralPath $work).ProviderPath
    $resolvedParent = (Resolve-Path -LiteralPath (Join-Path $out 'work')).ProviderPath
    if ((Split-Path -Parent $resolvedWork) -eq $resolvedParent -and
        (Split-Path -Leaf $resolvedWork) -match '^[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
