# 🐑 SheepCode · Sheep & Kuky 🌸

Tu pequeño taller local para leer código, proponer cambios y usar skills.

## Instalar

Ejecuta **SheepCode-Setup.exe** en Windows 10/11 de 64 bits, con una cuenta normal. No necesitas instalar .NET: el paquete incluye un runtime privado. Elige la carpeta del taller y la de los modelos; pueden estar en unidades distintas.

El asistente muestra CPU, RAM física, AVX2, gráficas DXGI y espacio libre. Recomienda un perfil que cabe dejando memoria para Windows y el contexto. Puedes escoger una alternativa más ligera o instalar solo el editor. Es una estimación por capacidad, no una comparativa que demuestre el modelo más rápido o de mayor calidad.

- Equipos con 4 GB o CPU sin AVX2: 🌱 Strata CPU con Qwen3-0.6B Q8_0. Motor x64/SSE2, sin servidor Python ni GPU, contexto 4096, hasta dos hilos y presupuesto de proceso 1536 MiB. Razonamiento oculto desactivado. Su compatibilidad se verifica con CPU emulada sin AVX y 4 GB; la emulación no mide la velocidad de un Celeron físico. Los modelos pequeños pueden cometer errores: pide cambios cortos y revisa las propuestas. Windows y las pestañas también necesitan memoria.
- Equipos pequeños con AVX2 y 8 GB o más: Qwen3 1.7B/4B por CPU o Vulkan verificado.
- Equipos con más memoria: Qwen3 8B/14B/32B, usando CPU o las gráficas que verifique Vulkan.
- Strata GPU/MoE: recomendado con al menos 48 GB de RAM, AVX2, una GPU compatible de 12 GB y 100 GB libres. Su instalación oficial prepara Qwen 3.8 Flash Next IQ2_XS. Requiere Internet y un controlador compatible; el setup no instala controladores. Strata CPU es un backend nuevo del fork, con biblioteca densa llama.cpp/ggml del commit fijado por Strata; no carga esos pesos MoE gigantes en 4 GB.
- Si existe el perfil original de SheepCode en este PC, el setup permite reutilizar sus pesos, Strata con RTX + RX 580 y la voz neuronal RX 580. Las instalaciones nuevas de Strata usan CUDA/HIP según su soporte; no se promete la mezcla RTX/RX antigua fuera del perfil original.

Los modelos se descargan de repositorios fijados por revisión y se verifican por SHA-256. Las descargas `.part` se reanudan al repetir la instalación. El editor y el código se instalan desde el paquete; la IA necesita completar la descarga seleccionada. El dictado opcional descarga Whisper small de 181 MiB. WebView2 se prepara con el instalador oficial de Microsoft si falta; también necesita Internet en ese caso.

La voz expresiva Ono_Anna permanece en la RX 580 cuando se reutiliza su instalación original. Un PC que carece del paquete neuronal RX 580 trabaja por texto y puede usar dictado; la lectura de voz figura **sin configurar** y no se sustituye por una voz diferente. Este setup no descarga ni convierte el paquete neuronal de voz.

## Qwen rápido ⚡

El perfil Strata CPU con Qwen3-0.6B usa un cálculo Q8 SSE2 optimizado, conserva la caché de tokens entre herramientas y exige los campos completos de cada acción. Mantiene contexto 4096 y presupuesto 1536 MiB.

La casilla **⚡ Qwen rápido** activa instrucciones breves, descubre las 55 skills a demanda y prelee el archivo abierto si lo mencionas. También puedes escribir o dictar y aceptar «Activa el modo rápido», «Desactiva el modo rápido» y «Estado del rendimiento». Requiere la skill models activa; el modelo solo consulta `performance_status`. Desactivar la casilla restaura instrucciones más amplias y el catálogo inicial; las mejoras SSE2 y de caché siguen en el motor.

Las propuestas conservan su diff y protección ante ediciones concurrentes. El tiempo de `performance_status` pertenece al motor; el flujo completo añade herramientas, GUI y voz. El modelo sigue siendo pequeño: revisa y prueba el código.

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

El panel **Skills** muestra 55 habilidades, un buscador, sus instrucciones y su estado. Algunas tienen herramientas locales; otras preparan código y configuración; las que usan servicios externos necesitan MCP. Puedes activarlas, desactivarlas, crear nuevas e importar un `SKILL.md` local. El catálogo adaptado se mantiene en `source/packaging/skills-catalog.json`; `sync-skills.ps1` regenera las 51 añadidas, conservando las cuatro base. La importación copia instrucciones: scripts, referencias y herramientas de otro entorno requieren adaptación.

Para controlar el PC, elige una ventana; para webs, usa las pestañas integradas. El texto de archivos y páginas no puede activar permisos ni aplicar cambios.

**Documentos y datos:** pide «Crea un DOCX con un título y tres párrafos», «Crea un Excel de gastos», «Prepara una presentación de tres diapositivas» o «Crea un PDF básico». Se muestran como propuestas; Aplicar guarda el binario y Deshacer recupera su versión anterior. El explorador ofrece una vista de contenido de DOCX/XLSX/PPTX/PDF. Word admite párrafos; Excel, valores numéricos, booleanos y texto; PowerPoint, títulos y puntos; PDF, texto latino básico. No recalcula fórmulas, no conserva formatos complejos al reconstruir un documento y no incluye OCR. Formato fino, edición avanzada, Google Docs/Slides y Excel en vivo requieren un proveedor compatible.

**Git y respaldos:** `git status`, `git log`, `git diff` y `git branches` consultan el repo abierto si Git está instalado. No ejecutan hooks ni modifican el repo. `Haz un respaldo del proyecto` prepara un ZIP y verifica su hash, con máximo 2000 archivos/100 MiB. Excluye secretos, enlaces, dependencias, Git y datos internos; es una copia del proyecto accesible, no un respaldo completo del equipo.

**Automatizaciones:** `Crea automatización revisar cada 15 minutos: Revisa el proyecto y prepara correcciones`. `Lista automatizaciones`, `Pausa automatización ID` y `Reanuda automatización ID` gestionan su estado. Se ejecutan con SheepCode abierto, motor listo y ese proyecto seleccionado; mínimo 5 minutos. Solo lectura y propuestas: no aplica cambios, controla PC/web, llama MCP ni cambia permisos en segundo plano.

## Conexiones MCP 🔗

En **Skills → Conexiones** configura una URL HTTPS (HTTP solo local) o la ruta absoluta de un `.exe` instalado con argumentos separados. Este cliente implementa inicialización, descubrimiento y llamadas de herramientas con JSON/Streamable HTTP y stdio. No incluye OAuth interactivo, sampling, elicitation o ejecución de recursos de skills. Cuentas/proveedores externos se configuran por separado. Especificación: [MCP transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports).

Ejemplo desactivado; reemplaza la URL y los nombres por los de tu servidor:

```json
[
  {
    "name": "imagenes",
    "url": "https://example.com/mcp",
    "command": "",
    "arguments": [],
    "tokenEnvironment": "MI_TOKEN_MCP",
    "skills": ["imagegen"],
    "allowedTools": [],
    "enabled": false,
    "timeoutSeconds": 30
  }
]
```

Guarda, activa la conexión cuando esté configurada y pulsa **Ver herramientas**. Copia a `allowedTools` únicamente los nombres exactos que autorizas, sin comodines. Los tokens viven en variables de entorno, fuera del JSON y del chat. La IA no puede editar estos ajustes. Cada llamada muestra servidor, herramienta y argumentos para autorizar esa operación. **Parar** cancela la petición y termina servidores stdio iniciados por SheepCode. No se repiten llamadas externas tras un fallo.

Comandos de texto, también utilizables tras aceptar un dictado:

`Analiza el sistema` · `Estado del modelo` · `Instala el modelo recomendado` · `Activa el motor` · `Desactiva el motor` · `Restaura el perfil anterior` · `Instala el dictado` · `Desactiva la skill models`.

Una instalación de modelo nueva guarda el perfil anterior cuando existe, y solo selecciona el nuevo al terminar. No ejecuta modelos candidatos para comparar rendimiento. El agente recibe el estado y la recomendación; descargar o cambiar un modelo exige la entrada humana directa.

## Actualizar, respaldo y quitar la aplicación

Abre **Skills → Actualizar** o escribe/dicta y acepta `Busca actualizaciones`, `Descarga la actualización` y `Instala la actualización`. Busca releases estables del repositorio público, verifica tamaño, SHA-256 y digest de GitHub cuando esté disponible, y vuelve a comprobar el archivo antes de ejecutarlo. La descarga se conserva en caché entre aperturas. Si se corta, puedes repetirla; el actualizador de setups no reanuda `.part`.

Al instalar se pide guardar el editor si tiene cambios y se cierra SheepCode antes de abrir el setup con la carpeta actual seleccionada. El modo actualización cambia app/source y **no descarga ni sustituye modelos**. Si está seleccionado el runtime estándar Strata CPU, instala también su binario verificado y respalda el anterior; conserva su perfil y sus pesos. Un ejecutable CPU con ruta personalizada se conserva y debe actualizarse manualmente. El setup guarda la versión anterior de app/source en `backups`; conserva ajustes, conexiones, skills, sesiones, propuestas, dictado, modelos y voz. Puedes revisar las ediciones antiguas de `source` en ese respaldo.

La búsqueda automática viene desactivada. Actívala con el checkbox o `Activa la búsqueda automática de actualizaciones`; consulta una vez al día mientras la app está abierta y avisa cuando haya una nueva release. `Desactiva la búsqueda automática de actualizaciones` la retira. Descargar e instalar siguen siendo acciones humanas. `Desactiva la skill updater` bloquea sus herramientas. También puedes abrir un setup descargado manualmente con `--target "D:\SheepCode" --upgrade`.

Las páginas largas se leen por fragmentos para caber en el contexto del modelo y dejar espacio para su respuesta. El agente conserva la pestaña y puede pedir otras partes con `browser_read`. Si intenta modificar un archivo existente sin haberlo leído, SheepCode lo lee primero y solicita una propuesta basada en ese contenido. El archivo sigue requiriendo **Aplicar**. **Parar** cancela la petición y termina el proceso propio del motor para cortar la generación; la siguiente tarea vuelve a cargarlo. Los cambios ya propuestos se conservan para revisión.

`Uninstall-SheepCode.ps1` elimina los binarios y accesos de esta instalación tras mostrar una confirmación. Conserva `source`, modelos, ajustes, sesiones, cambios y respaldos. No borra proyectos externos. El paquete de voz y los pesos compartidos del perfil original tampoco se eliminan.

Los informes de instalación y comprobaciones quedan en `checks`. Si se corta una descarga, abre de nuevo el setup para continuar. Si falta un campo de una acción del modelo, SheepCode pide corregirlo antes de ejecutar; si Strata agota su salida JSON, amplía el presupuesto y reintenta de forma acotada.

## Fuentes y licencias

SheepCode se entrega con código editable y licencia MIT para su código. Las dependencias conservan sus propias licencias en `licenses`, los runtimes y sus paquetes. Los modelos no están incluidos en el ejecutable: sus fichas y licencias se conservan en el catálogo.

Fuentes: [Strata](https://github.com/Niko1221/Strata), [requisitos y modelos](https://github.com/Niko1221/Strata/blob/main/docs/MODELS.md), [llama.cpp](https://github.com/ggml-org/llama.cpp), [modelos Qwen3](https://huggingface.co/Qwen/Qwen3-4B-GGUF), [Whisper](https://github.com/ggml-org/whisper.cpp), [.NET](https://dotnet.microsoft.com/download/dotnet/8.0), [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution), [estado de batería de Windows](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-system_power_status), [memoria gráfica DXGI](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_adapter_desc1).

El setup local no tiene firma Authenticode propia. Los instaladores de Python y WebView2 conservan sus firmas originales verificadas al construir el paquete. Comprueba el archivo `SHA256SUMS.txt` si transfieres la distribución.
