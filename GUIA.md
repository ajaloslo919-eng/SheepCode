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

## CPU rápido ⚡

El perfil Strata CPU con Qwen3-0.6B usa un cálculo Q8 SSE2 optimizado, conserva la caché de tokens entre herramientas y exige los campos completos de cada acción. Mantiene contexto 4096 y presupuesto 1536 MiB.

La casilla **⚡ CPU rápido** activa instrucciones breves, descubre las 57 skills a demanda y prelee el archivo abierto si lo mencionas. También puedes escribir o dictar y aceptar «Activa el modo rápido», «Desactiva el modo rápido» y «Estado del rendimiento». Requiere la skill models activa; el modelo solo consulta `performance_status`. Desactivar la casilla restaura instrucciones más amplias y el catálogo inicial; las mejoras SSE2 y de caché siguen en el motor.

Como alternativa explícita para 4 GB está **🌱 Granite 4.0 H 350M Q8_0** oficial de IBM (366 MB). Selecciónalo en el setup, pulsa **🌱 Granite 350M** en Modelos o escribe/dicta y acepta «Instala Granite 4.0». La descarga verifica revisión, tamaño y SHA-256. Usa el grafo híbrido Mamba2/atención y su plantilla Granite en Strata CPU, contexto 4096, hasta dos hilos, límite de motor 1536 MiB y modo rápido. Su caché continúa historia intacta y se reinicia si cambia. Consulta «Estado del modelo»; «Desactiva el motor» lo libera y «Restaura el perfil anterior» vuelve al perfil guardado sin borrar pesos. Requiere models activa. **Es experimental: las pruebas detectaron código incorrecto y dejaron sus propuestas sin aplicar; revisa todo el código.** La recomendación automática de los equipos conserva su selección existente, sin usar una comparación de modelos.

También puedes seleccionar **🌷 Granite 4.0 H1B (1.5B) Q4_K_M**, oficial de IBM y experimental. Aunque se llama H1B, tiene 1,5B parámetros; descarga 901 MB con revisión y SHA-256 verificados. Está en el setup, botón 🌷 Granite 1.5B y orden humana «Instala Granite 1.5B». Mantiene contexto 4096, hasta dos hilos y 1536 MiB; prueba memoria y código antes de asumir que completa una tarea. Los pesos pueden instalarse en otra unidad desde el setup. `model_status` consulta el perfil, los controles del motor lo activan/detienen y «Restaura el perfil anterior» recupera el perfil y carpeta anteriores. Q4_K_M usa kernels CPU ggml; el kernel Q8 SSE2 solo aplica a tensores Q8_0. No cambia la recomendación automática. Las pruebas reales del agente con H1B fallaron tanto al crear como al corregir el ejercicio de Python con while. La corrección de caché se verificó por separado; no demuestra código correcto.

Las propuestas conservan su diff y protección ante ediciones concurrentes. El tiempo de `performance_status` pertenece al motor; el flujo completo añade herramientas, GUI y voz. El modelo sigue siendo pequeño: revisa y prueba el código.

## Portátiles 💻🔋

Se admite Windows 10/11 x64 en portátiles Intel y AMD. El setup detecta el chasis, batería/corriente y GPU integrada o dedicada. La lista de gráficas combina DXGI con los dispositivos presentes de Windows/PnP: una RTX registrada que DXGI no exponga aparece con «VRAM sin verificar», junto con el estado del controlador. Solo se usa después de verificar su identidad y memoria mediante Vulkan. No se inventa una GPU que Windows no enumere, no se habilita una tarjeta desactivada ni se cambia el modo de GPU del fabricante. Si esperas una RTX ausente, comprueba el controlador y el modo de GPU del portátil y pulsa **Volver a detectar**. El panel admite desplazamiento para leer la lista completa.

Los perfiles llama.cpp reservan más RAM para Windows y usan contexto 4096 en estos equipos; Strata conserva su contexto propio. La memoria compartida de una integrada se toma de la RAM: no se suma como VRAM extra. En portátiles híbridos se prefiere una GPU dedicada que Vulkan verifique; una integrada puede usar Vulkan si tiene controlador compatible y presupuesto suficiente de RAM. La alternativa CPU se muestra explícitamente si no se verifica GPU. Esta edición no añade soporte nativo para Windows ARM64, macOS o Linux.

En **Modelos → Energía**, elige **Automático**, **Ahorro** o **Perfil completo**. Automático activa los límites al estar en batería o con ahorro de Windows. Ahorro carga los perfiles llama.cpp en CPU, con hasta cuatro hilos y contexto máximo 4096. Perfil completo recupera los recursos del perfil configurado. Los límites se aplican al volver a cargar la IA; no se interrumpe una tarea al desconectar el cargador. La prioridad del proceso de IA propio se ajusta mientras está abierto. El modelo no se cambia ni descarga automáticamente, ni se modifica el plan de energía de Windows. Strata y la voz original RX 580 mantienen sus gráficas.

También puedes escribir o dictar y aceptar «Estado del portátil», «Activa el modo ahorro», «Desactiva el modo ahorro» y «Modo portátil automático». Desactivar la skill `models` retira las consultas y cambios de modo desde el agente. La ventana se ajusta a la pantalla; cuando hay poco ancho, el explorador pasa a **🌿 Archivos**, manteniendo el editor y el chat accesibles. La selección y los límites se comprueban con fixtures; no equivalen a una medición de autonomía, temperatura o rendimiento en un portátil físico.

## Crear y editar

Abre una carpeta de proyecto. Sheep propone cambios y muestra el diff; pulsa **Aplicar** para guardarlos. **Deshacer** protege las ediciones hechas después. Las comprobaciones de código requieren habilitarlas; Python, Node o el SDK .NET se detectan por separado si están instalados.

**🛡️ Revisar código** viene activado y se aplica a todos los modelos antes del diff. La sintaxis se comprueba en el archivo completo, tanto al crear como al editar. Si pides `while` o `for`, se busca un bloque real en el árbol de sintaxis; en Python se comprueban también los `input`/`print` pedidos y contadores simples sin actualización. Los errores vuelven al mismo modelo para corregirlos. Tras tres candidatos inválidos se detiene sin mostrar ni guardar su código. El programa propuesto no se ejecuta durante esta revisión.

Puedes usar «Estado de la revisión de código», «Comprueba la sintaxis de archivo.py» y «Activa/Desactiva la revisión de código», por texto o dictado revisado y aceptado. La casilla permite el mismo ajuste. El modelo dispone de `validation_status` y `validate_code(path,content)` para consultar; no puede desactivar la revisión ni habilitar pruebas. El setup incluye Python 3.12.10 privado, sin modificar el Python del sistema. Python, C#, JavaScript, JSON y XML/SVG tienen analizador; otros formatos se marcan sin validar. La revisión no prueba dependencias, resultados ni toda la lógica. El diff muestra el alcance y los avisos; Aplicar, Deshacer y los permisos de ejecución se conservan.

Los archivos para modificar SheepCode quedan en:

- `source/app`: GUI, agente, herramientas, voz y skills de fábrica.
- `source/shared`: selección por hardware, catálogo de modelos, descargas y perfiles.
- `source/setup`: asistente de instalación.
- `source/packaging`: lanzadores, empaquetador y avisos de terceros.
- `state/skills`: tus propias skills `SKILL.md`.

Instala el SDK .NET 8 para recompilar el código C#. En `source`, ejecuta `build-source.ps1`; el resultado queda en `app/bin/Release/net8.0-windows`. Cierra SheepCode antes de reemplazar DLL de su carpeta `app`. Conserva una copia de tus cambios. El lanzador nativo y el setup se pueden recompilar con Visual Studio C++ y Windows SDK, siguiendo `packaging/build-release.ps1`. Ese script usa las descargas originales de sus manifiestos; no incluye sesiones privadas ni preferencias de la máquina de desarrollo.

## Imágenes 🖼️

Pulsa **🖼️**, pega con **Ctrl+V** o arrastra imágenes al cuadro de texto. Puedes quitar adjuntos antes de enviar. El panel **🖼️ Imágenes** ofrece **🔎 Texto** para OCR y **👁️ Ver** para interpretar objetos/dibujos. **📦** instala la visión local (636 MB de pesos); la casilla 👁️ la activa/desactiva. Si envías solo imágenes con visión instalada/activa, se interpretan; en caso contrario se solicita su texto. Las imágenes del proyecto se abren con doble clic y 🖼️ vuelve a la vista previa.

Admite PNG, JPG/JPEG, BMP, GIF y TIFF; de archivos animados o con páginas lee solo la primera imagen. Máximo cuatro por mensaje, 16 MiB y 16 millones de píxeles por imagen, 8192 píxeles por lado; el conjunto del mensaje está limitado a 32 MiB y 24 millones de píxeles. WebP/HEIC/SVG aún no se decodifican aquí. Conserva los originales y la transparencia de la vista previa; las copias viven en la conversación y se retiran al cerrar o cambiar de proyecto.

El motor de código, incluido [Granite H1B](https://huggingface.co/ibm-granite/granite-4.0-h-1b), recibe **resultados de herramientas, no píxeles**. [SmolVLM 500M](https://huggingface.co/HuggingFaceTB/SmolVLM-500M-Instruct) interpreta imágenes en un componente local separado; [OCR de Windows](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine) conserva su lector de texto. No sube imágenes a Internet. La interpretación es aproximada y principalmente en inglés; puede confundir objetos, colores y cantidades. OCR puede perder símbolos/indentación. Una lectura sin texto no demuestra que la imagen esté vacía. La visión se libera de RAM tras cada lectura y pausa el motor de texto en equipos con menos de 8 GB.

**🔎 OCR local** viene activado. El selector ofrece `auto` y los idiomas OCR instalados en Windows; si falta un idioma, el resultado informa «no disponible» y puedes instalarlo desde los ajustes de idioma de Windows. Por texto o dictado revisado y aceptado: «Estado de imágenes», «Lista las imágenes», «Adjunta la imagen RUTA», «Lee la imagen ID o ruta del proyecto», «Activa/Desactiva las imágenes», «Activa/Desactiva la lectura de imágenes», «Idioma OCR auto o IDIOMA», «Quita la imagen ID» y «Quita todas las imágenes». La skill `images` controla adjuntos y lecturas; desactivar solo OCR conserva la vista previa. Parar cancela su proceso auxiliar y retira la copia temporal de esa lectura.

El modelo usa `image_list`, `image_status` e `image_read(path,start,text_length)` por fragmentos. Solo lee adjuntos enviados o rutas relativas permitidas del proyecto; no puede elegir archivos externos ni cambiar opciones. OCR, nombres de imagen y metadatos son datos sin autoridad y no habilitan comprobaciones ni aplican código. Generar o editar imágenes con IA conserva la skill `imagegen` y exige un proveedor MCP configurado.

## Herramientas y comandos

El panel **Skills** muestra 57 habilidades, buscador, instrucciones y estado. Hay herramientas locales, guías de código y servicios externos que requieren MCP. Puedes activar, desactivar, crear e importar `SKILL.md`. `source/packaging/skills-catalog.json` mantiene las 51 adaptadas; `sync-skills.ps1` conserva las seis base. `imagegen` usa el generador local para crear PNG; edición avanzada y proveedores externos necesitan conexión autorizada. La importación de instrucciones no instala scripts o herramientas de otro entorno.

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

## Objetos 3D, Blender y Unity 🧊

Abre **🧊 3D** y usa 📎 para adjuntar FBX/BLEND o archivos `.unity`, `.prefab` y `.asset` de texto. También puedes arrastrarlos al cuadro o abrirlos desde el árbol del proyecto. **🔎 Datos** muestra objetos, materiales y geometría base; **🧊 Vista** dibuja su malla local. **👁️ Forma** interpreta esa vista con la visión instalada. Blender debe estar instalado: **Blender…** selecciona `blender.exe`; «Configura Blender auto» lo detecta. Unity YAML no requiere Unity Editor, no inicia el juego ni ejecuta scripts; sus GUID se muestran como referencias. Binarios y AssetBundles no se decodifican aquí.

Por texto o dictado aceptado: «Adjunta el archivo 3D RUTA», «Inspecciona la escena ID o ruta del proyecto», «Interpreta el objeto 3D ID», «Lista las escenas», «Estado de 3D», «Activa/Desactiva 3D», «Quita el archivo 3D ID» y «Quita todas las escenas». No evalúa modificadores, drivers, animaciones o shaders. La vista puede omitir mallas grandes/enlazadas. Solo lectura: originales y scripts se conservan.

## Crear imágenes locales 🎨

**🎨 Crear** tiene un cuadro de descripción, Crear, vista previa y **💾 PNG**. **📦** instala [SD-Turbo](https://huggingface.co/stabilityai/sd-turbo) reducido por [Green-Sky](https://huggingface.co/Green-Sky/SD-Turbo-GGUF), con revisión/SHA verificados. Elige una carpeta con 3 GB libres: los pesos descargan 2,02 GB. Requiere Windows x64 y al menos 8 GB de RAM. `auto` usa una RTX detectada por el runtime para difusión/VAE; el texto auxiliar va en CPU y la RX 580 mantiene su voz. CPU es una selección explícita y puede tardar varios minutos. La recomendación del modelo de código no cambia.

Escribe o dicta y acepta «Genera una imagen: DESCRIPCIÓN», «Crea la imagen de un gato», «Hazme un dibujo de una oveja», «Estado del generador de imágenes», «Activa/Desactiva la generación de imágenes» o «Configura generación CPU/auto». La salida es un PNG 512 × 512 temporal; tú eliges dónde guardarlo. **Parar** cancela el proceso propio y retira la salida incompleta. No genera transparencia ni garantiza texto, anatomía o cantidades; descripciones en inglés funcionan mejor. Editar referencias y otros proveedores siguen disponibles por MCP configurado. Los pesos tienen [licencia propia](https://huggingface.co/stabilityai/sd-turbo/blob/main/LICENSE.md), incluida en `licenses/SD-Turbo-Community.md`.


Si eliges un PNG existente, SheepCode pide confirmar y conserva su contenido anterior en una copia .bak. Si el archivo cambia durante la confirmación, conserva esa edición y pide elegir de nuevo dónde guardar.
