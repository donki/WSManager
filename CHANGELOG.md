# Changelog — sOC WSManager

## 2026.10.1.0 — Primera versión · First version

**Español**

- Convierte cualquier programa en un **servicio de Windows**, lo vigila y lo vuelve a arrancar si se
  cae, con una espera que crece si se cae en bucle (de 2 s a 256 s); pausar el servicio cancela la
  espera.
- **Icono junto al reloj** con todos tus servicios y su estado (punto verde, gris, ámbar o rojo):
  arrancar, parar, pausar, reiniciar, editar, abrir su salida y darlos de baja sin abrir nada más.
  Avisa si un servicio se para sin que nadie lo pida.
- **Editor por pestañas** con todas las opciones: cuenta (con el derecho de iniciar sesión como
  servicio), dependencias, prioridad y procesadores, parada escalonada (Ctrl+C, cerrar ventanas,
  terminar) y del árbol de procesos, qué hacer al salir según el código, ficheros de salida con hora
  en cada línea y rotación, entorno y ganchos.
- **Importa los servicios de otro gestor de servicios** conservando su configuración, a mano o
  **automáticamente** (una tarea programada al arrancar el equipo y cada 15 minutos), y se puede
  deshacer.
- **Línea de órdenes** para scripts con las órdenes de siempre (`install`, `set`, `get`, `start`,
  `dump`, `import --all`, `auto-import`…).
- Sin nada privilegiado escuchando: cada cambio pide permiso de administrador en ese momento, o se
  abre una ventana de administrador para varios cambios seguidos.
- Visor de eventos (origen sOCWSManager), español e inglés, tema claro y oscuro, guía de
  configuración y novedades.
- Por dentro: especificación y arquitectura escritas antes del código (SDD), 288 pruebas automáticas
  (90,4 % de la lógica) y 8 de interfaz.

**English**

- Turns any program into a **Windows service**, watches it and starts it again if it crashes,
  waiting longer if it crashes in a loop (from 2 s up to 256 s); pausing the service cancels the wait.
- **Icon next to the clock** with all your services and their state (green, grey, amber or red dot):
  start, stop, pause, restart, edit, open their output and remove them without opening anything
  else. It warns you if a service stops without being asked to.
- **Tabbed editor** with every option: account (with the log on as a service right), dependencies,
  priority and processors, staged stop (Ctrl+C, close windows, end) of the whole process tree, what
  to do on exit by exit code, output files with a timestamp on each line and rotation, environment
  and hooks.
- **Imports the services of another service manager** keeping their settings, by hand or
  **automatically** (a scheduled task at startup and every 15 minutes), and it can be undone.
- **Command line** for scripts with the usual commands (`install`, `set`, `get`, `start`, `dump`,
  `import --all`, `auto-import`…).
- Nothing privileged left listening: each change asks for administrator permission at that moment,
  or an administrator window can be opened for several changes in a row.
- Event Viewer (source sOCWSManager), Spanish and English, light and dark theme, setup guide and
  what's new.
- Under the hood: specification and architecture written before the code (SDD), 288 automated tests
  (90.4 % of the logic) and 8 UI tests.
