# sOC WSManager (sOCWSManager)

Convierte **cualquier programa en un servicio de Windows**, lo **vigila** y lo **vuelve a arrancar**
si se cae. Es el equivalente sOCratic de [NSSM](https://nssm.cc/) («the Non-Sucking Service
Manager»), en el que se inspira: guarda la configuración **con los mismos nombres del registro**,
tiene **la misma línea de órdenes** e **importa los servicios creados con NSSM** (y se puede
deshacer). Todo con un icono permanente en el área de notificación desde el que se gestionan los
servicios.

## Dónde conseguirla

- **Releases de GitHub** (exes autocontenidos de cada versión): https://github.com/donki/WSManager/releases
- No está en la Microsoft Store ni tiene MSIX (ver «Decisiones»).

## Qué hace

- **Servicio de verdad** (`sOCServiceHost.exe`, NativeAOT, 3 MB): habla con el administrador de
  servicios (SCM), lanza la aplicación con su carpeta, argumentos, entorno, prioridad, afinidad de
  CPU y consola, y la **vigila**: si sale, decide con `AppExit` (por defecto y **por código de
  salida**: `Restart`, `Ignore`, `Exit`, `Suicide`) y la relanza con **espera creciente** si se cae en
  bucle (0 → 2 → 4 … 256 s; pausar el servicio cancela la espera).
- **Parada escalonada**: Ctrl+C → `WM_CLOSE` → `WM_QUIT` → terminar, con sus tiempos y los pasos
  que se quieran saltar (`AppStopMethodSkip`), y el **árbol de procesos** entero.
- **Salidas a fichero** (stdout/stderr, juntas o separadas, con disposición de creación y **hora en
  cada línea**), **rotación** al arrancar, en marcha por tamaño o antigüedad, y bajo demanda.
- **Ganchos** (`AppEvents`: Start/Pre, Start/Post, Stop/Pre, Exit/Post, Rotate/Pre, Rotate/Post,
  Power/Change, Power/Resume) con las variables `NSSM_*` de NSSM, para que sus scripts sigan valiendo.
- **Visor de eventos**: origen `sOCWSManager` con arranques, paradas, muertes, reinicios y rotaciones.
- **Bandeja** (una sola instancia, arranque con Windows opcional `--tray`): cada servicio con su
  punto de color y sus acciones (arrancar, parar, pausar/continuar, reiniciar, editar, abrir
  stdout/stderr, dar de baja), crear, importar y abrir la ventana. Avisa si uno se para sin pedirlo.
- **Ventana principal** con estado, inicio, cuenta, PID del servicio y de la aplicación y reinicios.
- **Editor con las pestañas de NSSM**: Aplicación, Detalles, Inicio de sesión, Dependencias,
  Proceso, Parada, Acciones de salida, E/S, Rotación, Entorno y Ganchos.
- **Importar de NSSM**: lista los servicios cuyo `ImagePath` es `nssm.exe` y, con confirmación,
  cambia su `ImagePath` por el nuestro conservando todos sus parámetros (los que estaban en marcha se
  paran y se vuelven a arrancar). Se deshace mientras exista el `nssm.exe` original.
- **Importación automática** (apagada por defecto): una tarea programada que corre como SYSTEM al
  arrancar el equipo y cada 15 minutos importa sola los servicios de NSSM que aparezcan; los que estén
  en marcha pasan en su próximo arranque (o en el acto, si se marca). La bandeja la lanza al momento
  cuando ve uno nuevo y avisa de lo importado.
- Español e inglés, tema claro y oscuro, guía de configuración, novedades y «Acerca de».

## Línea de órdenes (compatible con NSSM)

`sOCServiceHost.exe` es de consola (espera y devuelve el código de salida: la indicada para
scripts); `sOCWSManager.exe` acepta lo mismo y se engancha a la consola de quien lo llama.

```
sOCServiceHost.exe install <servicio> [<programa> [<argumentos>…]]   (sin programa: abre la ventana)
sOCServiceHost.exe remove <servicio> [confirm]
sOCServiceHost.exe edit <servicio>
sOCServiceHost.exe start | stop | restart | pause | continue | status | statuscode | rotate | processes <servicio>
sOCServiceHost.exe get <servicio> <parámetro> [<subparámetro>]
sOCServiceHost.exe set <servicio> <parámetro> [<subparámetro>] <valor>…   (listas: +añadir -quitar :sustituir)
sOCServiceHost.exe reset <servicio> <parámetro> [<subparámetro>]
sOCServiceHost.exe list [all]          sOCServiceHost.exe dump <servicio> [<nombre nuevo>]
sOCServiceHost.exe import [list | --all | <servicio>…] [confirm]       (import-nssm también vale)
sOCServiceHost.exe undo-import <servicio> [confirm]
sOCServiceHost.exe auto-import on [--restart] | off | status | run
sOCServiceHost.exe setup               instala el componente de servicio en Archivos de programa
sOCServiceHost.exe debug <servicio>    la misma vigilancia en esta consola (Ctrl+C para)
```

Parámetros: los de `Parameters` de NSSM (`Application`, `AppDirectory`, `AppParameters`,
`AppPriority`, `AppAffinity`, `AppNoConsole`, `AppStopMethod*`, `AppKillProcessTree`, `AppThrottle`,
`AppExit`, `AppRestartDelay`, `AppStdin`, `AppStdout`, `AppStderr`, `App*CreationDisposition`,
`AppTimestampLog`, `AppRotate*`, `AppEnvironment`, `AppEnvironmentExtra`, `AppEvents`) y los del
servicio (`DisplayName`, `Description`, `Start`, `ObjectName`, `Type`, `DependOnService`,
`DependOnGroup`; `Name` e `ImagePath` solo se leen). Códigos de salida: 0 bien, 1 error, 2 no existe,
3 no es de WSManager, 5 hace falta administrador.

## Privilegios (sin nada privilegiado escuchando)

- La aplicación de la bandeja corre **sin elevar**. Ver la lista y el estado no pide nada.
- Cada operación que cambia algo (crear, editar, arrancar, parar, dar de baja, importar…) se hace en
  un **proceso elevado para esa operación** (`sOCWSManager.exe --elevated <lote>`, un aviso de UAC),
  que ejecuta las órdenes y se cierra. El lote es un fichero temporal **sin contraseñas**: si la
  cuenta del servicio la necesita, la pide el propio proceso elevado en su ventana.
- **Ejecutar como administrador** (botón del escudo): una ventana elevada aparte, sin bandeja, para
  hacer varios cambios con un solo UAC.
- El host se instala en `%ProgramFiles%\sOCWSManager\sOCServiceHost.exe` (solo los administradores
  pueden cambiarlo; un `ImagePath` en una carpeta del usuario sería una escalada de privilegios, y el
  SCM no puede leer ficheros de OneDrive bajo demanda).
- La importación automática es una tarea programada (no un servicio): se activa con un UAC, corre un
  momento y se cierra.

## Dónde guarda las cosas, a qué accede y qué puede romper

- **Servicios**: los guarda Windows (`HKLM\SYSTEM\CurrentControlSet\Services\<servicio>` y
  `…\Parameters`). WSManager **solo cambia servicios suyos** (`ImagePath` = `sOCServiceHost.exe`);
  con cualquier otro responde que no es suyo y no toca nada, salvo al importar de NSSM, con
  confirmación (o con la importación automática activada).
- **Estado en vivo**: `%ProgramData%\sOCWSManager\state\<servicio>.state` (PID de la aplicación,
  reinicios, último código).
- **Ajustes de la aplicación**: `%LOCALAPPDATA%\sOCWSManager\settings.txt` (idioma, versión vista,
  tamaño de ventana) y `errors.log`.
- **Contraseñas de cuentas**: van directas al SCM, que es quien las guarda. La aplicación no las
  escribe en ningún sitio.
- **Arranque con Windows**: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\sOCWSManager`.
- **Tarea programada** (si se activa la importación automática): `\sOCWSManager\AutoImportNssm`.
- No se conecta a nada. Sin cuenta, sin telemetría, sin anuncios.
- **Qué puede romper**: un servicio mal configurado arranca un programa con la cuenta elegida (con
  Sistema local puede hacer casi de todo); dar de baja un servicio no se puede deshacer (se pide
  confirmación); importar cambia el `ImagePath` de servicios de NSSM (se puede deshacer).

## Decisiones

- **Sin MSIX ni Microsoft Store**: un servicio en un paquete solo se puede declarar estático en el
  manifiesto (`desktop6:Service`) con la capacidad restringida `packagedServices`, no se pueden crear
  servicios arbitrarios; y el exe de un paquete vive en `WindowsApps\<paquete>_<versión>\`, así que
  cada actualización dejaría colgados los `ImagePath`. Se entrega como exes autocontenidos.
- Detalle en [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md); qué hace y por qué, en
  [docs/ESPECIFICACION.md](docs/ESPECIFICACION.md) (método SDD: primero la especificación).

## Compilar

```
dotnet build WSManager.slnx -c Debug -m:1 -nodeReuse:false
.\tools\entregar.ps1 -Version 2026.10.1.0 -Mensaje "…"     # publica, OneDrive, commit, push y release
```

Necesita el SDK de .NET 10. El host se publica con NativeAOT, que necesita las herramientas de C++
de Visual Studio (Build Tools); el script añade `vswhere` al PATH.

## Pruebas

`tests/WSManager.Tests` (xUnit): **288 pruebas** (286 pasan; 2 de integración que solo corren con
`SOC_WSM_INTEGRATION=1` en una consola elevada). Registro, configuración (incluida la que deja NSSM),
espera creciente, acciones de salida, parada escalonada con procesos reales (el programa de prueba
propio `sOCWSManagerTestApp.exe`: atiende o ignora Ctrl+C, tiene ventana, cola de mensajes, hijos…),
salidas y rotación, entorno, la línea de órdenes entera (también `dump` → volver a crear da lo mismo),
importación manual y automática con datos simulados, el host en modo `debug` de punta a punta, textos
es/en. El registro de prueba va en `HKCU\Software\sOCWSManagerTests\<guid>` y se borra; nunca toca
`HKLM` ni servicios reales.

- Cobertura de la lógica (`WSManager.Core`): **90,4 %** de líneas (4236 de 4684).
- Cobertura de toda la aplicación (lógica + host + interfaz): **66,1 %** (4236 de 6404 líneas de C#).
- Tiempo del banco: **unos 34 s** (`dotnet test --no-build`). Fecha: 2026-10-01.

`tests/WSManager.UITests` (FlaUI, modo aislado `SOC_SANDBOX`): **8 pruebas** de interfaz en unos
**48 s** (ver su [README](tests/WSManager.UITests/README.md)).

```
dotnet test tests\WSManager.Tests
dotnet test tests\WSManager.Tests --collect:"XPlat Code Coverage"
dotnet tool restore
dotnet tool run reportgenerator -reports:tests\WSManager.Tests\TestResults\*\coverage.cobertura.xml -targetdir:cobertura -reporttypes:TextSummary
dotnet test tests\WSManager.UITests
.\tools\prueba-real.ps1        # como administrador: servicios reales sOCWSManagerTest_*, que se borran
```

## Licencia

MIT (ver `LICENSE`). Terceros en `THIRD-PARTY-NOTICES.md`. Privacidad en `PRIVACY.md`.
