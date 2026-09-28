# Builds Quaykeep into .\app (framework-dependent, needs .NET 10 Desktop Runtime).
# Data stays in .\data next to this script, so rebuilding never touches your vault.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$running = Get-Process Quaykeep, SshManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$PSScriptRoot\app\*" }
if ($running) {
    Write-Host 'Quaykeep from .\app is running - exit it from the tray menu first.' -ForegroundColor Yellow
    exit 1
}

dotnet publish src/Quaykeep -c Release -o app --nologo
dotnet publish src/qk -c Release -o app --nologo
# files of SSH Manager, the name before Quaykeep; one in use (an old sshm.exe an AI agent runs) is renamed and removed later
foreach ($f in 'SshManager.exe','SshManager.dll','SshManager.pdb','SshManager.deps.json','SshManager.runtimeconfig.json',
                'SshManager.Core.dll','SshManager.Core.pdb','sshm.exe','sshm.dll','sshm.pdb','sshm.deps.json','sshm.runtimeconfig.json') {
    $path = Join-Path app $f
    if (Test-Path $path) { try { Remove-Item $path -Force -ErrorAction Stop } catch { Rename-Item $path "$f.$([guid]::NewGuid().ToString('N')).sshm-old" } }
}
Write-Host "`nDone: $PSScriptRoot\app\Quaykeep.exe" -ForegroundColor Green
