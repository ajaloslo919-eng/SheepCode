# 🌼 Validación de la entrega 0.3.0

Comprobaciones efectuadas el 5 de octubre de 2026 en Windows x64. Los informes privados conservan las rutas de instalación y preferencias; esta página recoge el alcance que se puede compartir.

## Paquete e instalación

- Compilación de la aplicación y del setup con cero errores, incluida una compilación del árbol de fuentes preparado para GitHub.
- Setup autoextraíble ejecutado para actualizar las instalaciones existentes de escritorio y AppData; preview del modo actualización en la carpeta actual.
- 254 archivos del payload verificados por tamaño y SHA-256; código fuente editable incluido.
- Preferencias conservadas exactamente durante la actualización y las pruebas. Perfil original de modelos, backend RTX + RX 580 y configuración de voz RX 580 conservados.
- Descargas fijadas por revisión y hash; pruebas deterministas de reanudación HTTP y rechazo de datos corruptos.

## Portátiles y gráficas

- Selección por capacidad para fixtures de 4, 8, 16 y 32 GB de RAM de portátil y perfiles de escritorio.
- iGPU con RAM compartida, equipo híbrido, dos GPU de escritorio, adaptador desconocido y GPU desactivada.
- Inventario DXGI combinado con dispositivos Windows/PnP presentes: VRAM desconocida permanece desconocida hasta validarse mediante Vulkan.
- Una exposición lógica duplicada de DXGI se reduce a una tarjeta física; dos tarjetas físicas idénticas se conservan.
- Modos automático, ahorro y perfil completo, con batería presente, ausente y estado desconocido.
- Permisos y desactivación de la skill de modelos comprobados por el diálogo de la aplicación.
- GUI real a 1366×728, 1024×650, 800×600 y 1520×900; editor, chat y compositor accesibles.
- Setup a 990×900 y 800×600 con fixture Intel + RTX no expuesta por DXGI: ambas gráficas legibles, desplazamiento hasta la última línea y botón de instalación visible.

Estas pruebas no certifican la RTX del portátil de una captura externa. No se ha medido autonomía, temperatura ni rendimiento en un portátil físico. La revisión visual se efectuó a 96 DPI.

## Regresiones de herramientas

- HTTP 502 `structured_output_failed`: más espacio para una acción completa, con un máximo de tres intentos y validación del campo `content`.
- Contexto 4096: reserva de salida, retirada de historial opcional y fragmentos web JSON válidos; se conservan la petición humana y la identidad de la pestaña.
- HTTP 400 `exceed_context_size_error`: un reintento acotado de inferencia con el contexto comunicado por el servidor. Las acciones ya ejecutadas no se repiten.
- `write_file` sobre un Python existente: prelectura real antes de pedir la nueva propuesta, conservando el archivo en disco hasta aplicar.
- Botón **Parar** real en la GUI: cancelación de una petición HTTP bloqueada, terminación de un proceso auxiliar propio, recuperación de Enviar y conservación de propuestas anteriores.

El HTTP y las acciones del modelo de estas regresiones son programados. Se ejecutan las herramientas y la GUI reales, sin cargar modelos candidatos. No equivalen a una prueba de Discord externo ni a una medición de rendimiento de IA.

## Skills, documentos, MCP y actualizador

- 55 skills cargadas sin errores; catálogo en el diálogo compartido por texto y dictado aceptado. GUI real con 55 tarjetas, buscador, estado local/MCP y ventana de actualización.
- DOCX, XLSX, PPTX, PDF, CSV, HTML y SVG: propuesta sin escribir, Aplicar, lectura de contenido y Deshacer. La edición binaria recupera los bytes originales y bloquea sustituciones sin lectura o con edición concurrente.
- Lectores independientes python-docx, openpyxl, python-pptx y pypdf abrieron los resultados. Se verificaron párrafos, diapositivas, valores numéricos/booleanos y texto que comienza por `=`; PDF validado y rasterizado con PDFium.
- XML con DTD bloqueado; el agente no puede editar ajustes, conexiones, permisos, cachés o binarios de su instalación.
- Consulta Git real sobre un repo de prueba, respaldo ZIP con hash y desactivación de skills comprobados en el dispatcher.
- Automatizaciones: creación, intervalo, proyecto seleccionado, pausa y bloqueo de acciones externas mediante acciones programadas. El disparo por temporizador de la GUI no se dejó funcionando durante un intervalo completo.
- MCP stdio: proceso propio real, inicialización, descubrimiento, ping, llamada, rechazo sin aprobación, nombres exactos autorizados, skill desactivada y cancelación. HTTP/SSE: transporte con respuestas programadas JSON multilínea, sesión y versión; resultado de herramienta y medio de imagen guardado sin base64 en el contexto.
- Actualizador: consulta y descarga con HTTP programado, tamaño/SHA-256/digest, rechazo de corrupción y cambios posteriores, ajuste de búsqueda automática y desactivación. El archivo de descarga del fixture nunca se ejecuta.

Las skills de flujo preparan código y archivos; las de servicios externos requieren un servidor, cuenta y herramientas autorizadas. No se certifican proveedores externos, generación de imágenes, OCR avanzado, Excel en vivo o servicios propietarios de Codex por el resultado de estos fixtures. El setup real actualizado se verificó por separado; no se certifica una actualización futura todavía inexistente.

## Agente instalado con su Strata activo

Se reprodujo el ejercicio de saludos sobre un archivo existente que contenía `for i in range(5): print(882)` en un proyecto de prueba propio. El agente instalado usó `read_file`, `write_file` y `finish`, preparó un programa con `input`, `while` y `print` y conservó el contenido original hasta aplicar la propuesta.

Después de aplicarla únicamente en ese proyecto de pruebas, Python verificó la estructura `while`, las entradas y los saludos con 0, 2 y 5 amigos. Se utilizó el modelo Strata activo del perfil original RTX 2060 SUPER + RX 580. Se verificó también la preparación del backend Ono_Anna en la RX 580; no se repitió síntesis ni reproducción de audio en esta prueba. Las preferencias originales se restauraron exactamente.

El mismo agente instalado cargó la skill `spreadsheets` y eligió `artifact_propose` para preparar `gastos.xlsx`, con hoja Gastos y valores Concepto/Importe/Luz/30. El archivo permaneció sin escribir hasta aplicar la propuesta en el proyecto de prueba. La lectura del documento confirmó los datos almacenados. Se ejecutó únicamente el Strata activo, sin candidatos alternativos.

## Reproducir comprobaciones

Después de compilar, sobre una instalación de pruebas propia:

```powershell
$env:SHEEPCODE_HOME = 'D:\SheepCode-pruebas'
.\app\bin\Release\net8.0-windows\SheepCode.exe --self-test
.\app\bin\Release\net8.0-windows\SheepCode.exe --protocol-check
.\app\bin\Release\net8.0-windows\SheepCode.exe --workflow-check
.\app\bin\Release\net8.0-windows\SheepCode.exe --catalog-check
.\app\bin\Release\net8.0-windows\SheepCode.exe --integrations-ui-check
.\app\bin\Release\net8.0-windows\SheepCode.exe --laptop-check
.\app\bin\Release\net8.0-windows\SheepCode.exe --laptop-ui-check
```

`--preflight-agent-check` usa el motor activo configurado, prepara y aplica cambios únicamente en un proyecto de prueba propio y comprueba Python y el XLSX. Requiere el modelo y Python instalados. No lo uses para comparar candidatos. `--update-check` consulta la release pública real, sin descargar ni instalar. `packaging/verify-artifacts.py` valida los archivos de `--catalog-check` con lectores independientes; esas librerías son herramientas de validación, no dependencias del generador local.

## Límites de esta entrega

Windows 10/11 x64 Intel/AMD. Sin certificación de todos los escalados o controladores. Las instalaciones nuevas no incluyen el paquete neuronal de voz RX 580 original. La consulta de auditoría de vulnerabilidades de NuGet devolvió NU1900 por falta de acceso al servicio; no se declara superada. El ejecutable del setup no tiene firma Authenticode propia.
