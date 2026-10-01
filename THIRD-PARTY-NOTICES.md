# Avisos de terceros — sOC WSManager

| Componente | Uso | Licencia | Titular |
|---|---|---|---|
| .NET 10 (runtime, WPF, NativeAOT) | Plataforma de la aplicación y del host. Va dentro de los exes autocontenidos. | MIT | .NET Foundation y colaboradores |
| API de Windows (advapi32, kernel32, user32, shell32, dwmapi) | Administrador de servicios, LSA (derecho de iniciar sesión como servicio), procesos, consola, ventanas, área de notificación, Visor de eventos. | API del sistema operativo | Microsoft |
| `EventLogMessages.dll` de .NET Framework | Fichero de mensajes del origen `sOCWSManager` del Visor de eventos. Es **del propio Windows**; no se distribuye. | API del sistema operativo | Microsoft |
| Programador de tareas (`schtasks.exe`) | La tarea de la importación automática. Viene con Windows; no se distribuye. | API del sistema operativo | Microsoft |
| Segoe Fluent Icons / Segoe MDL2 Assets | Iconos planos de los botones. Fuentes **del propio Windows**; no se distribuyen. | Fuente del sistema operativo | Microsoft |

Solo para las pruebas (no van en los exes): xUnit (Apache 2.0), Microsoft.NET.Test.Sdk (MIT),
coverlet (MIT), ReportGenerator (Apache 2.0), FlaUI (MIT).

Compatibilidad: guarda la configuración con los nombres de valor del registro de NSSM e importa sus
servicios; no incluye ni deriva código de NSSM. Todo el código (`*.cs`, `*.xaml`) es propio y va bajo
MIT (`LICENSE`).
