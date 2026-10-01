<#
.SYNOPSIS
    Prueba de verdad de sOC WSManager con el administrador de servicios de Windows (CA-01).
.DESCRIPTION
    Necesita una consola ELEVADA (lo comprueba). Crea servicios de prueba propios llamados
    sOCWSManagerTest_<aleatorio> que ejecutan el programa de prueba (sOCWSManagerTestApp.exe) y los
    borra al acabar, pase lo que pase. No toca ningún otro servicio.

    1. Compila en Debug (host, programa de prueba y pruebas).
    2. Instala el componente de servicio en %ProgramFiles%\sOCWSManager (orden setup).
    3. Ejecuta las pruebas de integración (SOC_WSM_INTEGRATION=1):
       - crear, arrancar, matar la aplicación y ver que se relanza, pausar, continuar, reiniciar,
         parar y dar de baja un servicio real;
       - importar un servicio cuyo ejecutable se llama nssm.exe, arrancarlo con WSManager, pararlo y
         deshacer la importación.
    4. Comprueba que no queda ningún sOCWSManagerTest_* (y borra los que hubiera).
.EXAMPLE
    # En PowerShell como administrador:
    D:\sOCProjects\Tools\WSManager\tools\prueba-real.ps1
#>
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz

dotnet build WSManager.slnx -c Debug -nologo -v q -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'No compila.' }

# El host que va a Archivos de programa es el de un solo fichero (NativeAOT): el Debug necesita sus DLL al lado.
$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;" + $env:PATH   # el enlazador de AOT busca vswhere
dotnet publish src\WSManager.Host -c Release -r win-x64 -nologo -v q -m:1 -nodeReuse:false -o publish\host
if ($LASTEXITCODE -ne 0) { throw 'No se publica el host.' }
& publish\host\sOCServiceHost.exe setup
if ($LASTEXITCODE -ne 0) { throw 'No se instala el componente de servicio.' }

try {
    $env:SOC_WSM_INTEGRATION = '1'
    dotnet test tests\WSManager.Tests --no-build -nologo --filter "FullyQualifiedName~IntegrationTests" --logger "console;verbosity=normal"
    $resultado = $LASTEXITCODE
}
finally {
    Remove-Item Env:\SOC_WSM_INTEGRATION -ErrorAction SilentlyContinue
    foreach ($s in Get-Service -Name 'sOCWSManagerTest_*' -ErrorAction SilentlyContinue) {
        Write-Warning "Quedaba $($s.Name): se borra."
        if ($s.Status -ne 'Stopped') { Stop-Service $s.Name -Force -ErrorAction SilentlyContinue }
        sc.exe delete $s.Name | Out-Null
    }
}
if ($resultado -eq 0) { 'Prueba real: todo bien.' } else { throw "Prueba real: ha fallado (código $resultado)." }
