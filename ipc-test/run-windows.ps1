# Cross-language IPC test on Windows. Needs .NET 8 SDK and JDK 21 (javac/java on PATH).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$shm = if ($args.Count -gt 0) { $args[0] } else { "$env:TEMP\valcraft-test.shm" }
Remove-Item $shm -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force build\java | Out-Null
javac -d build\java ..\neoforge-mod\src\main\java\dev\valcraft\link\ValCraftLink.java ..\neoforge-mod\src\main\java\dev\valcraft\link\BlockCodec.java java\IpcTestPeer.java java\BlockCodecTest.java
dotnet build csharp -c Release -o build\cs -v q --nologo | Out-Null
$j = Start-Process java -ArgumentList "-Dvalcraft.unpremultiply=false","-cp","build\java","IpcTestPeer",$shm -NoNewWindow -PassThru
dotnet build\cs\IpcTest.dll $shm 20000
$cs = $LASTEXITCODE
$j.WaitForExit()
Write-Host "csharp exit=$cs java exit=$($j.ExitCode)"
