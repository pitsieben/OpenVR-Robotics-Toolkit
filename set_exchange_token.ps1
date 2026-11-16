# Stores the GitHub plane-exchange token ENCRYPTED (Windows DPAPI, current user only)
# next to the installed plugin: %APPDATA%\Grasshopper\Libraries\ViveExchange\token.dat
# Only this Windows user on this PC can decrypt it. The token is never written in plain text.
Add-Type -AssemblyName System.Security
$dir = Join-Path $env:APPDATA 'Grasshopper\Libraries\ViveExchange'
New-Item -ItemType Directory -Force $dir | Out-Null
$secure = Read-Host 'Paste the GitHub token (input is hidden)' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim() }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
if (-not $token) { Write-Host 'No token entered. Nothing changed.'; pause; exit 1 }
$entropy = [Text.Encoding]::UTF8.GetBytes('ViveTcpRecorder.PlaneExchange.v1')
$enc = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($token), $entropy, 'CurrentUser')
$file = Join-Path $dir 'token.dat'
if (Test-Path $file) { (Get-Item $file -Force).Attributes = 'Normal' }
[IO.File]::WriteAllBytes($file, $enc)
(Get-Item $file -Force).Attributes = 'Hidden'
Remove-Item (Join-Path $dir 'token.txt') -ErrorAction SilentlyContinue
Write-Host "Token saved encrypted to $file"
Write-Host 'Restart Rhino if it is open. To remove the token, delete that file.'
pause
