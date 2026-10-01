# Pruebas de interfaz (FlaUI)

Recorridos de sOC WSManager con [FlaUI](https://github.com/FlaUI/FlaUI) (MIT) sobre UI Automation y
xUnit (General §8.7). Lanzan el **exe Debug ya compilado** en modo aislado y lo manejan como una
persona: crear, arrancar, pausar, parar, editar y dar de baja un servicio; el editor rechaza lo que no
vale y enseña sus once pestañas; cambio de idioma; «Acerca de» y novedades; la guía que sale la
primera vez; importar un servicio del otro gestor; activar y desactivar la importación automática.

## Cómo se lanzan

```powershell
dotnet build WSManager.slnx -c Debug -m:1 -nodeReuse:false
dotnet test tests\WSManager.UITests --no-build
```

Otro exe: variable `WSMANAGER_EXE` (tiene que ser Debug: en Release no hay modo aislado).
Son 8 pruebas y tardan unos 48 s; van una detrás de otra, cada una con su instancia y su carpeta.

## Modo aislado (`SOC_SANDBOX`)

Solo en Debug. La aplicación, con `SOC_SANDBOX=<carpeta temporal>`:

- usa un **SCM simulado** y un **registro simulado** (`registry.txt` en la carpeta, con las barras de
  los valores escapadas) en vez de los de Windows: crear, arrancar o borrar no toca ningún servicio
  real, y no pide UAC;
- guarda ajustes y errores en esa carpeta; el programador de tareas también es simulado;
- no pone icono en la bandeja, no tiene instancia única ni arranque con Windows, sus ventanas no se
  activan al abrir y lleva `[SOC_SANDBOX]` en el título.

## Capturas

Cada paso deja un PNG (PrintWindow, sin traer la ventana al frente) en `tests\WSManager.UITests\artifacts\`
(fuera de git), con el nombre `<prueba>-<paso>.png`.

## Cómo se buscan los controles

Por `AutomationId` (el `x:Name` en WPF); las filas de la lista, por el **nombre del servicio**; los
diálogos pequeños llevan `OkButton` y `CancelButton`. Nunca por el texto, que cambia con el idioma.
Todo va por patrones (Invoke, Value, SelectionItem, Toggle): sin ratón ni teclado.
