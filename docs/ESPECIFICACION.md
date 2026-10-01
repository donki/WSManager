# sOC WSManager — Especificación

> Método SDD: este documento dice **qué** tiene que hacer la aplicación; [ARQUITECTURA.md](ARQUITECTURA.md)
> dice **cómo**. Se escribe antes de programar y se mantiene al día: un cambio de comportamiento
> cambia primero aquí. Rige además toda la constitución (submódulo `constitution/`).
>
> **Versión de la especificación:** 2026-10-01 (para la 2026.10.01.0).

## 1. Qué es

Un gestor de servicios de Windows que convierte **cualquier programa** (un `.exe`, un script con su
intérprete, un servidor de consola) en un **servicio de Windows de verdad**, lo **vigila** y lo
**vuelve a arrancar** si se muere. Es el equivalente sOCratic del conocido gestor de servicios
libre que tanta gente usa para lo mismo, compatible con él:

- Guarda la configuración **con los mismos nombres de valor del registro** (`Application`,
  `AppDirectory`, `AppParameters`, `AppExit`, `AppStdout`, `AppRotateFiles`…) en
  `HKLM\SYSTEM\CurrentControlSet\Services\<servicio>\Parameters`.
- Tiene **la misma línea de órdenes** (`install`, `remove`, `set`, `get`, `start`, `dump`…).
- **Importa** los servicios creados con ese gestor (cambia el ejecutable del servicio por el nuestro
  y conserva todos sus parámetros), con vuelta atrás.

Piezas:

| Pieza | Qué es |
|---|---|
| `sOCServiceHost.exe` | El ejecutable que va como `ImagePath` del servicio. Habla con el administrador de servicios (SCM), lee su configuración del registro, lanza la aplicación y la vigila. También es la **línea de órdenes de consola** (como el original, un solo exe hace las dos cosas). |
| `sOCWSManager.exe` | La aplicación de gestión (WPF): icono permanente en el área de notificación con **una sola instancia**, ventana principal con la lista de servicios, editor por pestañas, importación, guía, novedades y «Acerca de». Acepta también las órdenes de la línea de órdenes. |

## 2. Usuarios e historias

- **H1. Convertir un programa en servicio.** Como administrador de un equipo quiero dar de alta un
  programa como servicio (ruta, carpeta, argumentos) para que arranque con Windows sin sesión
  iniciada.
- **H2. Que no se caiga.** Quiero que si el programa se muere vuelva a arrancar solo, sin entrar en
  un bucle que queme la CPU si muere nada más arrancar.
- **H3. Ver qué pasa.** Quiero ver de un vistazo qué servicios tengo, si están en marcha, su PID,
  cuántas veces se han reiniciado, y abrir su salida (stdout/stderr) sin buscar los ficheros.
- **H4. Gestionar desde la bandeja.** Quiero arrancar, parar, pausar, reiniciar, editar o dar de
  baja un servicio desde el menú del icono junto al reloj, sin abrir nada más.
- **H5. Cambiar de herramienta sin perder nada.** Tengo servicios hechos con el gestor original y
  quiero pasarlos a WSManager conservando su configuración, y poder volver atrás.
- **H6. Automatizar.** Quiero crear y configurar servicios desde un script con las mismas órdenes
  que ya conozco.
- **H7. Seguridad.** No quiero que la herramienta deje un servicio privilegiado escuchando, ni que
  guarde contraseñas, ni que toque servicios que no son suyos.

## 3. Requisitos funcionales

### 3.1 Servicio y vigilancia (host)

- **RF-01** El host se registra en el SCM como servicio propio (`SERVICE_WIN32_OWN_PROCESS`,
  opcionalmente `| SERVICE_INTERACTIVE_PROCESS`) y obtiene su nombre del propio SCM (no de la línea
  de órdenes), así que el mismo exe sirve para cualquier número de servicios.
- **RF-02** Lee `Parameters` del registro **al arrancar** el servicio. Sin `Application`, o si no
  existe el fichero, el servicio no arranca y lo apunta en el Visor de eventos.
- **RF-03** Lanza la aplicación con: directorio de inicio `AppDirectory` (por defecto, la carpeta
  del ejecutable), argumentos `AppParameters`, prioridad `AppPriority` (las seis clases:
  `REALTIME`, `HIGH`, `ABOVE_NORMAL`, `NORMAL`, `BELOW_NORMAL`, `IDLE`), afinidad `AppAffinity`
  (lista de CPU `0-1,3` o `All`) y consola (`AppNoConsole` = 1: sin consola).
- **RF-04** Entorno: `AppEnvironment` (REG_MULTI_SZ `CLAVE=valor`) **sustituye** el del servicio;
  `AppEnvironmentExtra` **añade o cambia** variables sobre el que haya, expandiendo `%VAR%`.
- **RF-05** E/S: `AppStdin`, `AppStdout`, `AppStderr` (rutas). `AppStdoutCreationDisposition` y
  `AppStderrCreationDisposition` (1 `CREATE_NEW`, 2 `CREATE_ALWAYS`, 3 `OPEN_EXISTING`,
  4 `OPEN_ALWAYS` = añadir, por defecto; 5 `TRUNCATE_EXISTING`). Si stdout y stderr son el mismo
  fichero se escriben en el mismo, sin pisarse. `AppTimestampLog` = 1 pone la hora
  (`aaaa-mm-dd hh:mm:ss.fff`) delante de cada línea.
- **RF-06** Rotación: `AppRotateFiles` = 1 rota los ficheros al arrancar el servicio (renombra a
  `nombre-aaaammddThhmmss.fff.ext`) si son más viejos que `AppRotateSeconds` o más grandes que
  `AppRotateBytes` (+ `AppRotateBytesHigh` × 2³²); con los dos a 0, siempre. `AppRotateOnline` = 1
  rota también **con el servicio en marcha** al pasar esos límites; `AppRotateOnline` = 2, además,
  **bajo demanda**. La orden `rotate` (control de usuario 128) rota en el acto si `AppRotateOnline`
  no es 0.
- **RF-07** Acciones de salida al morir la aplicación: `AppExit` (valor por defecto) y
  `AppExit\<código>` (por código de salida) con `Restart`, `Ignore`, `Exit` o `Suicide`:
  - `Restart` (por defecto): vuelve a lanzarla, con espera (RF-08).
  - `Ignore`: no la relanza; el servicio sigue «en marcha» sin aplicación.
  - `Exit`: para el servicio limpiamente.
  - `Suicide`: el host sale **sin** decir que se para, para que el SCM aplique las acciones de
    recuperación del servicio.
- **RF-08** Espera antes de relanzar: si la aplicación vivió menos de `AppThrottle` ms (1500 por
  defecto), cada salida rápida seguida dobla la espera: 0 → 2 s → 4 s → 8 s … hasta **256 s**. Una
  ejecución que pasa de `AppThrottle` vuelve a 0. `AppRestartDelay` (ms) es la espera mínima
  siempre. **Pausar el servicio** durante la espera la cancela (no relanza) y **continuar** lanza
  en el acto.
- **RF-09** Parada escalonada al parar el servicio (y al reiniciar): 1) Ctrl+C a su consola, 2)
  `WM_CLOSE` a sus ventanas, 3) `WM_QUIT` a sus hilos, 4) `TerminateProcess`. Cada paso espera
  `AppStopMethodConsole`, `AppStopMethodWindow` y `AppStopMethodThreads` ms (1500 por defecto) a que
  salga. `AppStopMethodSkip` salta pasos (bits 1 consola, 2 ventanas, 4 hilos, 8 terminar).
  `AppKillProcessTree` (1 por defecto) aplica lo mismo a todos sus descendientes.
- **RF-10** Mientras para, el host le dice al SCM cuánto le falta (`dwWaitHint`/`dwCheckPoint`)
  para que no lo dé por colgado.
- **RF-11** Ganchos `AppEvents\<evento>\<acción>` (REG_EXPAND_SZ, una orden): `Start/Pre`,
  `Start/Post`, `Stop/Pre`, `Exit/Post`, `Rotate/Pre`, `Rotate/Post`, `Power/Change`,
  `Power/Resume`. Se lanzan con las variables `NSSM_*` del original (nombre del servicio, evento,
  acción, PID, código de salida, contadores, tiempo en marcha…) para que los scripts existentes
  sigan valiendo. Esperan como mucho 60 s. Si `Start/Pre` sale con código **99**, la aplicación no
  se lanza y el servicio se para.
- **RF-12** Pausar/continuar: pausar con la aplicación en marcha la **para** (escalonado) y deja el
  servicio en pausa; con la espera de reinicio en curso, la cancela. Continuar la lanza.
  *(Ampliación sobre el original, que solo acepta la pausa durante la espera.)*
- **RF-13** Visor de eventos: origen propio **`sOCWSManager`** en el registro de Aplicación. Se
  apuntan arranques y paradas del servicio, lanzamientos y muertes de la aplicación (con su código
  y tiempo en marcha), reinicios con su espera, rotaciones y errores de configuración.
- **RF-14** Estado en vivo: el host deja en `%ProgramData%\sOCWSManager\state\<servicio>.json` el
  PID de la aplicación, cuándo arrancó, cuántas veces se ha relanzado y el último código de salida,
  para que la interfaz lo enseñe sin privilegios.
- **RF-15** Modo de depuración: `sOCServiceHost.exe debug <servicio>` ejecuta la misma vigilancia
  en primer plano (sin SCM) con la configuración del registro; Ctrl+C la para.

### 3.2 Línea de órdenes (compatible)

`sOCServiceHost.exe` (consola: espera y devuelve código de salida, la indicada para scripts) y
`sOCWSManager.exe` (se engancha a la consola de quien lo llama) aceptan:

| Orden | Qué hace |
|---|---|
| `install <servicio> [<programa> [<argumentos>…]]` | Crea el servicio. Sin programa abre la ventana de alta. |
| `remove <servicio> [confirm]` | Da de baja. Sin `confirm` pregunta en una ventana. |
| `edit <servicio>` | Abre el editor de ese servicio. |
| `start`, `stop`, `restart`, `pause`, `continue <servicio>` | Control del servicio. |
| `status <servicio>` / `statuscode <servicio>` | Estado (`SERVICE_RUNNING`…); el segundo lo devuelve como código de salida. |
| `rotate <servicio>` | Rotación bajo demanda (control 128). |
| `get <servicio> <parámetro> [<subparámetro>]` | Lee un parámetro. |
| `set <servicio> <parámetro> [<subparámetro>] <valor>…` | Lo cambia. En listas (`DependOnService`, `DependOnGroup`, `AppEnvironment`, `AppEnvironmentExtra`) varios valores; con `+valor` añade, `-valor` quita y `:valor` sustituye. |
| `reset <servicio> <parámetro> [<subparámetro>]` | Vuelve al valor por defecto. |
| `processes <servicio>` | PID y nombre del host y de todos sus descendientes. |
| `list [all]` | Servicios de WSManager (o todos). |
| `dump <servicio> [<nombre nuevo>]` | Las órdenes que recrean el servicio. |
| `import-nssm [list \| <servicio>… \| all] [confirm]`, `undo-import <servicio> [confirm]` | Importar del gestor original y deshacer (ver 3.4). |
| `help`, `version` | Ayuda y versión. |

- **RF-20** Los nombres de parámetro son los del registro más los del servicio: `Name` (solo leer),
  `ImagePath` (solo leer), `DisplayName`, `Description`, `Start` (`SERVICE_AUTO_START`,
  `SERVICE_DELAYED_AUTO_START`, `SERVICE_DEMAND_START`, `SERVICE_DISABLED`), `ObjectName`
  (`set … ObjectName <cuenta> [<contraseña>]`), `Type` (`SERVICE_WIN32_OWN_PROCESS`,
  `SERVICE_INTERACTIVE_PROCESS`), `DependOnService`, `DependOnGroup`.
- **RF-21** `AppPriority` se escribe y se lee por nombre (`HIGH_PRIORITY_CLASS`); en el registro va
  el valor numérico de la clase, como el original.
- **RF-22** Mayúsculas y minúsculas dan igual en órdenes, parámetros y valores con nombre.
- **RF-23** Códigos de salida: 0 bien; 1 error de uso o de datos; 2 el servicio no existe; 3 no es
  de WSManager; 5 hace falta ser administrador; para `statuscode`, el estado.
- **RF-24** Las órdenes que cambian algo **solo actúan sobre servicios de WSManager** (su
  `ImagePath` apunta a `sOCServiceHost.exe`). Con cualquier otro servicio responden que no es suyo
  (código 3) y no tocan nada. Excepción: la importación (3.4), que pide confirmación.

### 3.3 Interfaz

- **RF-30 Bandeja.** Al arrancar, `sOCWSManager.exe` pone su icono en el área de notificación y se
  queda ahí aunque se cierre la ventana; **una sola instancia** por sesión (la nueva versión cierra
  a la vieja, General §8.3). `--tray` arranca sin ventana (es lo que usa «Arrancar con Windows»).
- **RF-31** Menú del icono: un apartado por servicio de WSManager con su **estado y un punto de
  color** (verde en marcha, gris parado, ámbar pausado o en transición, rojo si se paró sin
  pedirlo), y dentro arrancar, parar, pausar/continuar, reiniciar, editar, abrir stdout, abrir
  stderr y dar de baja; y además **crear nuevo**, **importar**, **abrir la ventana** y **salir**.
  Clic izquierdo abre la ventana.
- **RF-32** La lista se refresca sola (sondeo ligero cada 2 s mientras la ventana o el menú están a
  la vista, cada 10 s si no). Si un servicio de WSManager se para sin que se haya pedido desde la
  aplicación, avisa con un globo.
- **RF-33 Ventana principal:** lista con estado, nombre, nombre visible, tipo de inicio, cuenta,
  PID del servicio, PID de la aplicación y reinicios; botones de icono (sin palabras) para crear,
  editar, arrancar, parar, pausar, continuar, reiniciar, rotar, abrir registros, dar de baja,
  importar, ejecutar como administrador, guía, novedades, idioma y «Acerca de».
- **RF-34 Editor** con las pestañas del original: Aplicación, Detalles, Inicio de sesión,
  Dependencias, Proceso, Parada, Acciones de salida, E/S, Rotación, Entorno y Ganchos. Cada campo
  valida al salir de él y al guardar (General §6.8): lo que no vale se dice y no se guarda.
- **RF-35** Contraseña de la cuenta con el ojo para verla (General §6.6). **No se guarda en
  ningún sitio de la aplicación**: va directa al SCM, que es quien la guarda.
- **RF-36** Español e inglés (cambio al momento, se recuerda), tema claro y oscuro según Windows,
  iconos planos, «Acerca de» canónico con aviso legal, **Novedades** (General §6.7) y **Guía**
  (General §6.10) con: instalar el host, arrancar con Windows, crear el primer servicio e importar.
- **RF-37** Los errores se dicen en el idioma del usuario, con la razón y qué hacer (General §6.9);
  lo técnico va a `%LOCALAPPDATA%\sOCWSManager\errors.log`. Gestor global de excepciones (§6.12).

### 3.4 Importar del gestor original

- **RF-40** Detecta los servicios cuyo `ImagePath` apunta a un ejecutable llamado `nssm.exe` (con
  o sin comillas y argumentos) y los enseña con su programa, estado y si su configuración es
  importable.
- **RF-41** Con confirmación, para cada servicio elegido: anota el `ImagePath` original en
  `Parameters\sOCWSManagerImportedFrom` (para deshacer), cambia el `ImagePath` a
  `sOCServiceHost.exe` **conservando todos los parámetros**; si estaba en marcha, lo para antes y
  lo vuelve a arrancar después.
- **RF-42** Deshacer: mientras exista el `nssm.exe` anotado, vuelve a ponerlo como `ImagePath`
  (parando y arrancando si estaba en marcha) y borra la anotación. Si ya no existe, lo dice y no
  hace nada.
- **RF-43** Un servicio que el original habría rechazado (sin `Application`) se enseña como «no
  importable» y no se toca.

#### Importación automática (petición de Josep, 2026-10-01)

- **RF-44** Interruptor «Importar automáticamente los servicios de otro gestor de servicios»
  (apagado por defecto) en la ventana de importar, y `auto-import on [--restart] | off | status | run`
  en la línea de órdenes. Activarlo pide UAC **una vez**.
- **RF-45** Con él activado, al arrancar el equipo y cada 15 minutos se importan solos (sin
  confirmar uno a uno) los servicios cuyo `ImagePath` apunta a `nssm.exe`, conservando todos sus
  parámetros. Un fallo con uno no para a los demás y se reintenta en la siguiente vuelta.
- **RF-46** Los que están **en marcha** no se tocan por defecto: el cambio vale desde su próximo
  arranque (normalmente el del equipo), el momento de menos riesgo. Con «Reiniciar en el acto»
  (`--restart`) se paran y se vuelven a arrancar como en la importación manual.
- **RF-47** Si la aplicación de la bandeja ve un servicio nuevo del otro gestor, lanza la importación
  sin esperar a la vuelta (como mucho una vez por minuto), y avisa con un globo de lo importado. Se
  deshace igual que la manual.
- La orden `import [list | --all | <servicio>…] [confirm]` (con `import-nssm` como sinónimo) es la
  de la importación manual; la tarea usa `import --all --auto [--restart]`.

### 3.5 Privilegios

- **RF-50** La aplicación de la bandeja corre **sin elevar**. Leer la lista y el estado no pide
  nada. Cada operación que cambia algo en el SCM o en `HKLM` se ejecuta en un **proceso elevado
  para esa operación** (`sOCWSManager.exe --elevated <lote>`, aviso de UAC), que hace el lote de
  órdenes y se cierra. No queda ningún proceso ni servicio auxiliar privilegiado escuchando.
- **RF-51** «Ejecutar como administrador»: abre una segunda ventana elevada, sin bandeja, que hace
  las operaciones directamente (sin un aviso de UAC por cada una); al cerrarla se va.
- **RF-52** Si la cuenta necesita contraseña y la operación va al proceso elevado, la pide **el
  proceso elevado** en su propia ventana: la contraseña nunca viaja por ficheros, tuberías ni la
  línea de órdenes.
- **RF-53** El host se instala en `%ProgramFiles%\sOCWSManager\sOCServiceHost.exe` (solo
  administradores pueden escribir ahí): un `ImagePath` en una carpeta del usuario (OneDrive,
  Descargas) sería una escalada de privilegios y además el SCM no puede leer ficheros de OneDrive
  bajo demanda. Al instalar o actualizar se copia si el de al lado de la aplicación es más nuevo;
  si el viejo está en uso, se aparta (`.old`) y los servicios lo cogen al reiniciarse.
- **RF-54** Al conceder una cuenta con contraseña, se le da el derecho **«Iniciar sesión como
  servicio»** (`SeServiceLogonRight`) si no lo tenía.

## 4. Requisitos no funcionales

- **RNF-01** .NET 10, Windows 10 2004+ / 11 x64. Host autocontenido de un solo fichero
  (NativeAOT: pequeño y arranca rápido); aplicación autocontenida de un solo fichero.
- **RNF-02** MIT y solo dependencias MIT/Apache o APIs del sistema (THIRD-PARTY-NOTICES.md).
- **RNF-03** Sin red: la aplicación no se conecta a nada. Sin cuenta, sin telemetría.
- **RNF-04** El sondeo de la bandeja no pasa del 1 % de una CPU.
- **RNF-05** Banco xUnit (§8.6) con cobertura medida, sin tocar `HKLM`: el registro de prueba va
  en `HKCU\Software\sOCWSManagerTests\<guid>` y se borra; las pruebas reales del SCM solo con
  `SOC_WSM_INTEGRATION=1` y elevado, con servicios `sOCWSManagerTest_*` que se borran al acabar.
- **RNF-06** Pruebas de interfaz FlaUI (§8.7) en modo aislado `SOC_SANDBOX` (solo Debug): SCM y
  registro simulados en una carpeta temporal, sin bandeja, sin elevar, sin arranque con Windows.

## 5. Casos límite

| # | Caso | Qué se hace |
|---|---|---|
| CL-01 | La aplicación muere al instante en bucle | Espera creciente hasta 256 s (RF-08); cada muerte al Visor de eventos. |
| CL-02 | Se pide parar durante la espera de reinicio | Se cancela la espera y el servicio se para sin lanzar nada. |
| CL-03 | Se pausa durante la espera | Se cancela; queda en pausa; continuar lanza ya. |
| CL-04 | La aplicación ignora Ctrl+C, no tiene ventanas ni cola de mensajes | Se llega a `TerminateProcess` tras los tiempos configurados. |
| CL-05 | La aplicación deja hijos | Con `AppKillProcessTree`=1 se paran también (los hijos nacidos después que el padre, para no matar un PID reutilizado). |
| CL-06 | `AppExit` con un código no listado | Se usa el valor por defecto; si no hay, `Restart`. Valor desconocido → `Restart` y aviso en el Visor. |
| CL-07 | stdout y stderr al mismo fichero | Un solo escritor; las líneas no se mezclan a medias. |
| CL-08 | Fichero de salida en una carpeta que no existe | Se crea la carpeta; si no se puede, se apunta el error y la aplicación arranca sin esa salida. |
| CL-09 | `AppRotateBytes` = 0 y `AppRotateSeconds` = 0 con `AppRotateFiles`=1 | Se rota siempre al arrancar (nunca en marcha por tamaño). |
| CL-10 | Rotar dos veces en el mismo milisegundo | Nombre único (se añade `-1`, `-2`). |
| CL-11 | `AppAffinity` con CPU que no existen | Se quedan las que existen; si no queda ninguna, todas, con aviso. |
| CL-12 | `AppEnvironmentExtra` con `PATH=%PATH%;C:\x` | Se expande sobre el entorno que ya hay. |
| CL-13 | Línea de `AppEnvironment` sin `=` | Se ignora con aviso. |
| CL-14 | Servicio interactivo con una cuenta que no es LocalSystem | Se rechaza al guardar (el SCM no lo admite). |
| CL-15 | Dependencia de grupo sin `+` | Se le añade al guardar (`+grupo`), como pide el SCM. |
| CL-16 | Dar de baja un servicio en marcha | Se para (escalonado) y luego se borra; el SCM lo quita al cerrarse los descriptores. |
| CL-17 | Nombre de servicio con espacios, `/` o `\` | Espacios sí (entre comillas); `/` y `\` no (los rechaza el SCM). Máximo 256 caracteres. |
| CL-18 | El usuario cancela el UAC | Se dice «Operación cancelada: hace falta permiso de administrador» y no se cambia nada. |
| CL-19 | Importar un servicio que ya es de WSManager | No sale en la lista. |
| CL-20 | Deshacer cuando `nssm.exe` ya no está | Se avisa y no se toca. |
| CL-21 | `set` sobre un servicio que no es de WSManager | Código 3, nada cambia. |
| CL-22 | Abrir registros sin `AppStdout` configurado | Aviso «Este servicio no guarda su salida en un fichero» con qué hacer. |
| CL-23 | Dos `sOCWSManager.exe` a la vez | Se queda una; la otra la pone delante y se va (§8.3). |
| CL-24 | `ObjectName` con cuenta de dominio sin contraseña en la línea de órdenes | Error de uso; las cuentas integradas, virtuales (`NT SERVICE\…`) y gestionadas (`…$`) no la piden. |
| CL-25 | El fichero de `AppStdin` no existe | La aplicación arranca con la entrada vacía y se apunta en el Visor. |

## 6. Criterios de aceptación

- **CA-01** Con un programa de prueba propio, `install`, `start`, `status` = `SERVICE_RUNNING`,
  matar la aplicación → vuelve a arrancar sola y el contador de reinicios sube; `pause` → la
  aplicación se para y el estado es `SERVICE_PAUSED`; `continue` → vuelve; `stop` → para en menos de
  los tiempos configurados; `remove confirm` → el servicio deja de existir. (Script
  `tools\prueba-real.ps1`; necesita UAC.)
- **CA-02** `dump` de un servicio y ejecutar esas órdenes con otro nombre da la misma configuración
  (prueba automática con el registro de prueba).
- **CA-03** La configuración escrita por el original (valores reales del registro, simulados en las
  pruebas) se lee igual: aplicación, carpeta, salidas, disposición, `AppExit` por defecto y por
  código.
- **CA-04** La espera de reinicio sigue 0, 2, 4, 8 … 256, 256 s con salidas rápidas, y vuelve a 0
  tras una ejecución larga.
- **CA-05** La parada escalonada se detiene en el primer método que funciona: un proceso que
  atiende Ctrl+C no recibe `WM_CLOSE`, uno con ventana sale con `WM_CLOSE`, uno sordo acaba
  terminado; con los bits de `AppStopMethodSkip` se salta lo indicado.
- **CA-06** Las claves de texto es/en son las mismas, sin huecos distintos.
- **CA-07** La interfaz arranca en modo aislado, crea, edita, arranca/para y da de baja un servicio
  simulado, cambia de idioma y abre «Acerca de», sin tocar el SCM real.
- **CA-08** Ninguna orden de cambio actúa sobre un servicio ajeno (prueba con servicio simulado
  ajeno).
