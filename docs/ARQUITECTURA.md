# sOC WSManager — Arquitectura

> El **cómo** de [ESPECIFICACION.md](ESPECIFICACION.md). Los identificadores RF/CL/CA remiten a ella.

## 1. Proyectos

```
WSManager/
├── WSManager.slnx
├── src/
│   ├── WSManager.Core/        biblioteca (net10.0-windows): toda la lógica, sin interfaz
│   ├── WSManager.Host/        sOCServiceHost.exe: servicio + línea de órdenes de consola (NativeAOT)
│   └── WSManager.App/         sOCWSManager.exe: WPF, bandeja, ventanas
├── tests/
│   ├── WSManager.Tests/       xUnit (§8.6) sobre Core
│   ├── WSManager.TestApp/     sOCWSManagerTestApp.exe: programa de prueba con conductas a la carta
│   └── WSManager.UITests/     FlaUI (§8.7) en modo aislado
├── tools/                     entregar.ps1, prueba-real.ps1
└── docs/                      ESPECIFICACION.md, ARQUITECTURA.md
```

**Toda la lógica vive en `WSManager.Core`** (constitución §5 y §7): los dos exes son cáscaras que
la enchufan a Windows. Así el banco de pruebas mide de verdad lo que importa.

## 2. Core por capas

| Carpeta | Contenido |
|---|---|
| `Model/` | `ServiceConfig` (todo lo del servicio: lo del SCM y lo de `Parameters`), enumeraciones (`StartType`, `ExitAction`, `PriorityClass`, `StopMethods`…), `ServiceInfo` (lo que se lista), `ServiceState`. |
| `Registry/` | `IRegistry` (claves y valores con su tipo) con `WinRegistry` (una raíz real: `HKLM\SYSTEM\CurrentControlSet\Services` en producción, `HKCU\Software\sOCWSManagerTests\<guid>` en pruebas) y `MemoryRegistry` (pruebas y modo aislado, guardable en JSON). `Names` tiene **los nombres de valor del original**. `ParametersStore` lee y escribe `Parameters` sin perder lo que no entiende. |
| `Scm/` | `IServiceManager` (crear, borrar, leer y cambiar configuración, controlar, estado, enumerar) con `ScmServiceManager` (P/Invoke a `advapi32`) y `SandboxServiceManager` (servicios simulados en JSON para el modo aislado y las pruebas). `LogonRight` (LSA). |
| `Supervision/` | La vigilancia: `Supervisor` (máquina de estados asíncrona), `ThrottlePolicy`, `ExitActionResolver`, `StopPlan`, `IAppLauncher`/`IAppProcess` con `Win32AppLauncher` (CreateProcess), `ProcessTree`, `StopMethods` (Ctrl+C, WM_CLOSE, WM_QUIT, Terminate), `EnvironmentBuilder`, `Affinity`, `CommandLine`, `HookRunner`, `StateFile`. |
| `Output/` | `LogFile` (disposición de creación, rotación por tamaño/tiempo/demanda, nombres únicos), `OutputPump` (tubería → fichero, con hora por línea), `Rotation`. |
| `Cli/` | `CliParser` (órdenes → `CliCommand`), `CliRunner` (las ejecuta contra `IServiceManager` + `ParametersStore`), `ParameterCatalog` (get/set/reset de cada parámetro con su formato), `Dump`. |
| `Import/` | `NssmImport`: detectar, planificar, convertir y deshacer. |
| `Platform/` | `EventLogSink` (Visor de eventos), `HostInstaller` (copia a Program Files, origen de eventos, carpeta de estado y sus permisos), `Elevation`. |
| `Localization/` | `Loc` es/en (la usan la línea de órdenes y la interfaz). |
| `Services/` | Lo de la aplicación que no es interfaz: `AppSettings`, `AppLog`, `Sandbox`, `WhatsNew`, `ServiceListModel` (lista + cambios de estado para los globos), `Validation`. |

## 3. El host (`sOCServiceHost.exe`)

```
Main(args)
 ├─ args vacíos → StartServiceCtrlDispatcher
 │     ├─ falla con 1063 (no lo lanzó el SCM) → ayuda de la línea de órdenes
 │     └─ ServiceMain(argv[0] = nombre real del servicio)
 │           RegisterServiceCtrlHandlerEx  (STOP, PAUSE_CONTINUE, SHUTDOWN, POWEREVENT, 128 = rotar)
 │           ParametersStore(HKLM) → ServiceConfig
 │           Supervisor.RunAsync(control)  ← canal de controles del SCM
 │           SetServiceStatus en cada transición (con wait hint durante la parada)
 ├─ "debug <servicio>" → Supervisor en primer plano, Ctrl+C para (RF-15)
 └─ otra cosa → CliRunner (consola; código de salida = resultado)
```

Se escribe el despachador a mano por P/Invoke y no con `ServiceBase`: `ServiceBase` se come
`argv[0]`, y sin él no sabemos qué servicio somos (el mismo exe sirve para todos, RF-01). Además se
necesitan los eventos de energía y el control 128, y NativeAOT queda limpio sin reflexión.

### 3.1 Supervisor

```
          ┌──────────── continuar ─────────────┐
          ▼                                    │
 [Arrancando] ──lanzada──► [En marcha] ──pausa──► [En pausa]
     ▲   │ Start/Pre=99         │ muere                ▲
     │   ▼                      ▼                      │
     │ [Parando]◄──parar── [Decidir AppExit]           │
     │                     │  Restart │ Ignore │ Exit │ Suicide
     │                     ▼          ▼        ▼       ▼
     └──── espera ──── [Espera] [Sin app]  [Parando] [salida sin SERVICE_STOPPED]
                         │ pausa ────────────────────────┘
```

- Los controles (parar, pausar, continuar, rotar, energía) llegan por un `Channel` y la espera de
  reinicio es un `Task.Delay` cancelable: parar o pausar la cortan (CL-02, CL-03).
- `ThrottlePolicy.Next(runtime, throttleMs, restartDelayMs, quickExits)` es una función pura:
  salida rápida → `quickExits+1` y espera `min(2^n, 256)` s; si no, 0; y siempre como mínimo
  `AppRestartDelay`.
- `ExitActionResolver` mira `AppExit\<código>`, luego el valor por defecto, luego `Restart`.
- `StopPlan.For(config)` da la lista de pasos (método, espera) según `AppStopMethodSkip`; quien
  la ejecuta (`StopMethods`) para en cuanto el proceso sale. Con `AppKillProcessTree` se hace una
  foto del árbol **antes** de parar al padre (los hijos se quedan huérfanos al morir este) y se
  paran también, solo los nacidos después que su padre (CL-05).

### 3.2 Lanzamiento (`Win32AppLauncher`)

`CreateProcessW` con `CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | <prioridad>` y
`CREATE_NEW_CONSOLE` (ventana oculta) o `DETACHED_PROCESS` si `AppNoConsole`. Afinidad con
`SetProcessAffinityMask` antes de `ResumeThread`. stdin: el fichero abierto y heredable. stdout y
stderr: **tuberías** que lee el host (`OutputPump`), lo que permite la hora por línea y la rotación
en marcha; si los dos van al mismo fichero comparten `LogFile` (con cerrojo por línea, CL-07).

Ctrl+C: el host (servicio, sin consola) hace `AttachConsole(pid)`,
`SetConsoleCtrlHandler(null, true)`, `GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)`, `FreeConsole` y
restaura el manejador; todo bajo un cerrojo global porque un proceso solo tiene una consola.

## 4. La aplicación (`sOCWSManager.exe`)

- **Arranque** (`App.OnStartup`): gestor global de excepciones (§6.12) → `Sandbox.Apply()` → si
  `--elevated <lote>`: ejecutar el lote y salir (sin bandeja ni instancia única) → si trae una
  orden de la línea de órdenes: `AttachConsole(ATTACH_PARENT_PROCESS)`, `CliRunner`, salir → si
  `--admin`: ventana elevada sin bandeja → si no: `SingleInstance.Claim()` (§8.3), bandeja,
  ventana (salvo `--tray`), novedades/guía.
- **Bandeja** (`TrayIcon`): `Shell_NotifyIcon` permanente; el menú es un `ContextMenu` de WPF
  (estilos propios: tema claro/oscuro, iconos de Segoe Fluent Icons, punto de color por estado)
  abierto en el cursor sobre la ventana oculta de mensajes.
- **Operaciones** (`Operations`): cada acción de la interfaz se traduce a órdenes de la línea de
  órdenes (`string[]`). Si el proceso está elevado (o en modo aislado) las ejecuta `CliRunner` en
  el sitio; si no, `Elevation.RunBatch` escribe el lote en `%TEMP%` (sin contraseñas), lanza
  `sOCWSManager.exe --elevated <lote> <resultado>` con `runas`, espera y lee el resultado. Un solo
  aviso de UAC por acción del usuario, aunque sean varias órdenes (alta = `install` + `set`…).
- **Contraseñas** (RF-52): la orden `set <svc> ObjectName <cuenta>` sin contraseña, dentro de un
  lote elevado, abre en ese proceso la ventana de contraseña. La contraseña existe solo en memoria
  del proceso elevado y se entrega al SCM.
- **Lista y estado**: `ServiceListModel.Refresh()` = `EnumServicesStatusEx` (sin privilegios) +
  lectura de `ImagePath`/`Start`/`ObjectName` del registro + `StateFile`. Sondeo con
  `DispatcherTimer` (2 s a la vista, 10 s escondida).
- **Modo aislado** (`SOC_SANDBOX`, solo Debug): `SandboxServiceManager` + `MemoryRegistry` en la
  carpeta temporal; sin bandeja, sin instancia única, sin UAC, sin arranque con Windows; las
  ventanas no se activan al abrir; `[SOC_SANDBOX]` en el título.

## 5. Decisiones

| Decisión | Por qué |
|---|---|
| Host con **NativeAOT** | Un exe de pocos MB que arranca al instante; un servicio por exe, y puede haber muchos. |
| Host en `%ProgramFiles%\sOCWSManager` | Seguridad (RF-53) y que el SCM lo pueda leer siempre (no OneDrive). |
| **Sin MSIX** ni Microsoft Store | Un servicio en un paquete solo se puede declarar **estático** en el manifiesto (`desktop6:Service`) con la capacidad restringida `packagedServices` (y `localSystemServices` para LocalSystem), que la Store revisa a mano; no se pueden crear servicios arbitrarios. Además el exe de un paquete vive en `WindowsApps\<paquete>_<versión>\`: cada actualización cambia la ruta y dejaría colgados los `ImagePath`. Se entrega como exes autocontenidos. |
| Lote elevado por UAC en vez de servicio auxiliar | RF-50/H7: nada privilegiado escuchando. |
| `ContextMenu` de WPF para la bandeja | Submenús por servicio con punto de color, iconos planos y tema; con menús Win32 habría que dibujar a mano. |
| Visor de eventos con `EventLogMessages.dll` de .NET Framework | Viene con Windows 10/11 y tiene un mensaje «%1» para cualquier identificador: el texto sale entero sin compilar un fichero de mensajes propio. |
| Variables `NSSM_*` en los ganchos | Compatibilidad con los scripts ya escritos para el original. |
| Pausa = parar la aplicación | Ampliación documentada (RF-12): la bandeja ofrece pausar siempre. |
| Importación automática con una **tarea programada como SYSTEM** (RF-44 a RF-47) | Necesita administrador y no puede haber nada privilegiado esperando (RF-50). Se descartaron: (1) hacerlo en cada `sOCServiceHost.exe`, porque un servicio no debe tocar otros servicios ni depender de que haya alguno de WSManager en marcha; (2) un servicio vigilante propio, que sería justo lo prohibido; (3) la aplicación de la bandeja elevada, que obligaría a correr elevada siempre. La tarea (`\sOCWSManager\AutoImportNssm`, `schtasks.exe` con XML) se crea en el único UAC que pide activarla, corre al arrancar el equipo y cada 15 minutos `sOCServiceHost.exe import --all --auto [--restart]` y se cierra (como mucho 10 minutos, una instancia a la vez). Su descriptor de seguridad deja a los usuarios autenticados **leerla y lanzarla**, no cambiarla: así la bandeja, sin elevar, la lanza en cuanto ve un servicio nuevo del otro gestor. Lanzarla solo puede importar, que es lo que el administrador ya decidió. Desactivarla borra la tarea. |
