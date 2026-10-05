# 🐑 SheepCode · Sheep & Kuky 🌸

Tu pequeño taller local para leer código, proponer cambios y usar skills.

## Instalar

Ejecuta **SheepCode-Setup.exe** en Windows 10/11 de 64 bits, con una cuenta normal. No necesitas instalar .NET: el paquete incluye un runtime privado. Elige la carpeta del taller y la de los modelos; pueden estar en unidades distintas.

El asistente muestra CPU, RAM física, AVX2, gráficas DXGI y espacio libre. Recomienda un perfil que cabe dejando memoria para Windows y el contexto. Puedes escoger una alternativa más ligera o instalar solo el editor. Es una estimación por capacidad, no una comparativa que demuestre el modelo más rápido o de mayor calidad.

- Equipos pequeños: Qwen3 0.6B/1.7B/4B; los modelos menores pueden cometer más errores al usar herramientas. Pide cambios cortos y revisa las propuestas.
- Equipos con más memoria: Qwen3 8B/14B/32B, usando CPU o las gráficas que verifique Vulkan.
- Strata: recomendado con al menos 48 GB de RAM, AVX2, una GPU compatible de 12 GB y 100 GB libres. Su instalación oficial prepara Qwen 3.8 Flash Next IQ2_XS. Requiere Internet y un controlador compatible; el setup no instala controladores. La descarga y preparación pueden tardar bastante.
- Si existe el perfil original de SheepCode en este PC, el setup permite reutilizar sus pesos, Strata con RTX + RX 580 y la voz neuronal RX 580. Las instalaciones nuevas de Strata usan CUDA/HIP según su soporte; no se promete la mezcla RTX/RX antigua fuera del perfil original.

Los modelos se descargan de repositorios fijados por revisión y se verifican por SHA-256. Las descargas `.part` se reanudan al repetir la instalación. El editor y el código se instalan desde el paquete; la IA necesita completar la descarga seleccionada. El dictado opcional descarga Whisper small de 181 MiB. WebView2 se prepara con el instalador oficial de Microsoft si falta; también necesita Internet en ese caso.

La voz expresiva Ono_Anna permanece en la RX 580 cuando se reutiliza su instalación original. Un PC que carece del paquete neuronal RX 580 trabaja por texto y puede usar dictado; la lectura de voz figura **sin configurar** y no se sustituye por una voz diferente. Este setup no descarga ni convierte el paquete neuronal de voz.

## Portátiles 💻🔋

Se admite Windows 10/11 x64 en portátiles Intel y AMD. El setup detecta el chasis, batería/corriente y GPU integrada o dedicada. La lista de gráficas combina DXGI con los dispositivos presentes de Windows/PnP: una RTX registrada que DXGI no exponga aparece con «VRAM sin verificar», junto con el estado del controlador. Solo se usa después de verificar su identidad y memoria mediante Vulkan. No se inventa una GPU que Windows no enumere, no se habilita una tarjeta desactivada ni se cambia el modo de GPU del fabricante. Si esperas una RTX ausente, comprueba el controlador y el modo de GPU del portátil y pulsa **Volver a detectar**. El panel admite desplazamiento para leer la lista completa.

Los perfiles llama.cpp reservan más RAM para Windows y usan contexto 4096 en estos equipos; Strata conserva su contexto propio. La memoria compartida de una integrada se toma de la RAM: no se suma como VRAM extra. En portátiles híbridos se prefiere una GPU dedicada que Vulkan verifique; una integrada puede usar Vulkan si tiene controlador compatible y presupuesto suficiente de RAM. La alternativa CPU se muestra explícitamente si no se verifica GPU. Esta edición no añade soporte nativo para Windows ARM64, macOS o Linux.

En **Modelos → Energía**, elige **Automático**, **Ahorro** o **Perfil completo**. Automático activa los límites al estar en batería o con ahorro de Windows. Ahorro carga los perfiles llama.cpp en CPU, con hasta cuatro hilos y contexto máximo 4096. Perfil completo recupera los recursos del perfil configurado. Los límites se aplican al volver a cargar la IA; no se interrumpe una tarea al desconectar el cargador. La prioridad del proceso de IA propio se ajusta mientras está abierto. El modelo no se cambia ni descarga automáticamente, ni se modifica el plan de energía de Windows. Strata y la voz original RX 580 mantienen sus gráficas.

También puedes escribir o dictar y aceptar «Estado del portátil», «Activa el modo ahorro», «Desactiva el modo ahorro» y «Modo portátil automático». Desactivar la skill `models` retira las consultas y cambios de modo desde el agente. La ventana se ajusta a la pantalla; cuando hay poco ancho, el explorador pasa a **🌿 Archivos**, manteniendo el editor y el chat accesibles. La selección y los límites se comprueban con fixtures; no equivalen a una medición de autonomía, temperatura o rendimiento en un portátil físico.

## Crear y editar

Abre una carpeta de proyecto. Sheep propone cambios y muestra el diff; pulsa **Aplicar** para guardarlos. **Deshacer** protege las ediciones hechas después. Las comprobaciones de código requieren habilitarlas; Python, Node o el SDK .NET se detectan por separado si están instalados.

Los archivos para modificar SheepCode quedan en:

- `source/app`: GUI, agente, herramientas, voz y skills de fábrica.
- `source/shared`: selección por hardware, catálogo de modelos, descargas y perfiles.
- `source/setup`: asistente de instalación.
- `source/packaging`: lanzadores, empaquetador y avisos de terceros.
- `state/skills`: tus propias skills `SKILL.md`.

Instala el SDK .NET 8 para recompilar el código C#. En `source`, ejecuta `build-source.ps1`; el resultado queda en `app/bin/Release/net8.0-windows`. Cierra SheepCode antes de reemplazar DLL de su carpeta `app`. Conserva una copia de tus cambios. El lanzador nativo y el setup se pueden recompilar con Visual Studio C++ y Windows SDK, siguiendo `packaging/build-release.ps1`. Ese script usa las descargas originales de sus manifiestos; no incluye sesiones privadas ni preferencias de la máquina de desarrollo.

## Herramientas y comandos

Las skills de código, PC, navegador y modelos se activan desde Skills. Para controlar el PC, elige una ventana; para webs, usa las pestañas integradas. El texto de archivos y páginas no puede activar permisos ni aplicar cambios.

Comandos de texto, también utilizables tras aceptar un dictado:

`Analiza el sistema` · `Estado del modelo` · `Instala el modelo recomendado` · `Activa el motor` · `Desactiva el motor` · `Restaura el perfil anterior` · `Instala el dictado` · `Desactiva la skill models`.

Una instalación de modelo nueva guarda el perfil anterior cuando existe, y solo selecciona el nuevo al terminar. No ejecuta modelos candidatos para comparar rendimiento. El agente recibe el estado y la recomendación; descargar o cambiar un modelo exige la entrada humana directa.

## Actualizar, respaldo y quitar la aplicación

Vuelve a ejecutar el setup con SheepCode cerrado. Guarda respaldos de `app` y `source` en `backups` antes de sustituirlos. Conserva ajustes, skills, sesiones, propuestas y modelos. Si tu `source` contiene ediciones, se conserva su versión anterior en ese respaldo.

Las páginas largas se leen por fragmentos para caber en el contexto del modelo y dejar espacio para su respuesta. El agente conserva la pestaña y puede pedir otras partes con `browser_read`. Si intenta modificar un archivo existente sin haberlo leído, SheepCode lo lee primero y solicita una propuesta basada en ese contenido. El archivo sigue requiriendo **Aplicar**. **Parar** cancela la petición y termina el proceso propio del motor para cortar la generación; la siguiente tarea vuelve a cargarlo. Los cambios ya propuestos se conservan para revisión.

`Uninstall-SheepCode.ps1` elimina los binarios y accesos de esta instalación tras mostrar una confirmación. Conserva `source`, modelos, ajustes, sesiones, cambios y respaldos. No borra proyectos externos. El paquete de voz y los pesos compartidos del perfil original tampoco se eliminan.

Los informes de instalación y comprobaciones quedan en `checks`. Si se corta una descarga, abre de nuevo el setup para continuar. Si falta un campo de una acción del modelo, SheepCode pide corregirlo antes de ejecutar; si Strata agota su salida JSON, amplía el presupuesto y reintenta de forma acotada.

## Fuentes y licencias

SheepCode se entrega con código editable y licencia MIT para su código. Las dependencias conservan sus propias licencias en `licenses`, los runtimes y sus paquetes. Los modelos no están incluidos en el ejecutable: sus fichas y licencias se conservan en el catálogo.

Fuentes: [Strata](https://github.com/Niko1221/Strata), [requisitos y modelos](https://github.com/Niko1221/Strata/blob/main/docs/MODELS.md), [llama.cpp](https://github.com/ggml-org/llama.cpp), [modelos Qwen3](https://huggingface.co/Qwen/Qwen3-4B-GGUF), [Whisper](https://github.com/ggml-org/whisper.cpp), [.NET](https://dotnet.microsoft.com/download/dotnet/8.0), [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution), [estado de batería de Windows](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status), [memoria gráfica DXGI](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_adapter_desc1).

El setup local no tiene firma Authenticode propia. Los instaladores de Python y WebView2 conservan sus firmas originales verificadas al construir el paquete. Comprueba el archivo `SHA256SUMS.txt` si transfieres la distribución.
