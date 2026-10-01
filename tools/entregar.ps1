<#
.SYNOPSIS
    Entrega de sOC WSManager: versión, banco de pruebas, exes autocontenidos, OneDrive, commit,
    push y release de GitHub.
.DESCRIPTION
    El CHANGELOG se escribe antes a mano (su primera sección son las notas de la release). Sin MSIX
    (ver README, «Decisiones»). Mismo guion que el de RC Manager: si se toca uno, mirar el otro.
    Publica: sin marcha atrás.
.EXAMPLE
    .\tools\entregar.ps1 -Version 2026.10.1.0 -Mensaje "2026.10.1.0: primera versión"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Mensaje
)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz
[IO.Directory]::SetCurrentDirectory($raiz)

# 1. Versión (una sola, en Directory.Build.props) y novedades.
$p = 'Directory.Build.props'; $x = Get-Content $p -Raw
foreach ($t in 'Version', 'AssemblyVersion', 'FileVersion') { $x = [regex]::Replace($x, "<$t>[^<]*</$t>", "<$t>$Version</$t>") }
[IO.File]::WriteAllText((Resolve-Path $p), $x)
$primera = (Get-Content src\WSManager.Core\whatsnew.json -Raw | ConvertFrom-Json)[0].version
if ([version]$primera -ne [version]$Version) {
    throw "whatsnew.json empieza por $primera y no por ${Version}: escribe antes las novedades (General 6.7)."
}

# 2. Banco de pruebas en verde (General 8.6): condición para dar la versión por buena.
dotnet build WSManager.slnx -c Debug -nologo -v q -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'No compila.' }
dotnet test tests\WSManager.Tests --no-build -nologo
if ($LASTEXITCODE -ne 0) { throw 'El banco de pruebas no pasa.' }

# 3. Exes: host NativeAOT (necesita vswhere para el enlazador) y aplicación de un solo fichero.
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH
Remove-Item -Recurse -Force publish -ErrorAction SilentlyContinue
dotnet publish src\WSManager.Host -c Release -r win-x64 -nologo -v q -m:1 -nodeReuse:false -o publish\host
if ($LASTEXITCODE -ne 0) { throw 'No se publica el host.' }
dotnet publish src\WSManager.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -nologo -v q -m:1 -nodeReuse:false -o publish\app
if ($LASTEXITCODE -ne 0) { throw 'No se publica la aplicación.' }
$hostExe = 'publish\host\sOCServiceHost.exe'
$appExe = 'publish\app\sOCWSManager.exe'

# 4. OneDrive (General 8): los dos exes juntos (la aplicación busca el host a su lado) y el LEEME.
$d = 'C:\ID\OneDrive\WSManager'
New-Item -ItemType Directory -Force $d | Out-Null
$abierta = Get-Process sOCWSManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$d*" }
$abierta | Stop-Process -Force
Start-Sleep -Milliseconds 500
foreach ($f in $appExe, $hostExe) {
    try { Copy-Item $f $d -Force -ErrorAction Stop }
    catch {
        $nueva = [IO.Path]::GetFileNameWithoutExtension($f) + '-nueva.exe'
        Copy-Item $f (Join-Path $d $nueva) -Force
        Write-Warning "$(Split-Path $f -Leaf) estaba en uso: la versión nueva queda como $nueva"
    }
}
$leeme = @"
sOC WSManager
=============

Versión $Version

Qué es
------
Convierte cualquier programa en un servicio de Windows, lo vigila y lo vuelve a arrancar si se cae.
Icono junto al reloj con tus servicios y su estado, editor por pestañas con todas las opciones,
importación de los servicios de otro gestor de servicios (a mano o automática) y línea de órdenes.

Qué es cada fichero
-------------------
- sOCWSManager.exe: la aplicación (ventana e icono de la bandeja). Ábrela con doble clic.
- sOCServiceHost.exe: el componente de servicio y la línea de órdenes de consola. Tiene que estar
  al lado de sOCWSManager.exe. Al crear el primer servicio se copia solo a
  C:\Program Files\sOCWSManager (los servicios ejecutan esa copia).

Cómo ejecutarlo
---------------
Basta con abrir sOCWSManager.exe; no hay que instalar nada. Cada cambio en los servicios pide
permiso de administrador. Windows puede avisar de que el editor es desconocido ("Windows protegió su
PC"): pulsa "Más información" y "Ejecutar de todas formas".

Si hay un fichero -nueva.exe
----------------------------
La aplicación estaba abierta al copiar la versión nueva: ciérrala (Salir, en el icono del reloj),
borra el viejo y quítale "-nueva" al nuevo.

Software libre bajo licencia MIT. En español y en inglés, claro y oscuro.
"@
[IO.File]::WriteAllText("$d\LEEME.txt", $leeme, (New-Object Text.UTF8Encoding $false))
"OneDrive: " + ((Get-ChildItem $d | ForEach-Object { $_.Name }) -join ', ')

# 5. Git y release (General 8: la release es lo que queda de cada versión).
git add -A
git commit -q -m $Mensaje
git push -q origin HEAD 2>&1 | Select-Object -Last 1
python 'D:\sOCProjects\Mobile\Shared\release-github.py' "v$Version" $appExe $hostExe 2>&1 | Select-Object -Last 1

# 6. La entrega vuelve a dejar la aplicación abierta si lo estaba (General 8.3).
if ($abierta) {
    Start-Process "$d\sOCWSManager.exe" -ArgumentList '--tray'
    Start-Sleep 3
    $v = (Get-Process sOCWSManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$d*" } | Select-Object -First 1).FileVersion
    "Abierta de nuevo: $v"
}
