# Build MVNCP with the .NET Framework compiler (no Visual Studio needed)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$bin  = Join-Path $root 'bin'

New-Item -ItemType Directory -Force -Path $bin | Out-Null

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe from .NET Framework not found' }

$flags = @('/nologo', '/optimize+', '/codepage:65001')
$refs  = @('/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')

Write-Host 'Building mvncp-server.exe...'
& $csc @flags /target:winexe "/out:$bin\mvncp-server.exe" "$src\Common.cs" "$src\Server.cs" @refs
if ($LASTEXITCODE -ne 0) { throw 'mvncp-server.exe build failed' }

Write-Host 'Building mvncp-viewer.exe...'
& $csc @flags /target:winexe "/out:$bin\mvncp-viewer.exe" "$src\Common.cs" "$src\Viewer.cs" @refs
if ($LASTEXITCODE -ne 0) { throw 'mvncp-viewer.exe build failed' }

Write-Host 'Building mvncp-test.exe...'
& $csc @flags /target:exe "/out:$bin\mvncp-test.exe" "$src\Common.cs" "$src\TestClient.cs" @refs
if ($LASTEXITCODE -ne 0) { throw 'mvncp-test.exe build failed' }

$ini = Join-Path $bin 'mvncp-server.ini'
if (-not (Test-Path $ini)) {
    Copy-Item (Join-Path $root 'mvncp-server.ini.example') $ini
    Write-Host 'Created bin\mvncp-server.ini from the example - change the password!'
}

Write-Host ''
Write-Host "Done. Files in: $bin"
Get-ChildItem $bin | Select-Object Name, Length | Format-Table -AutoSize
