# 🌼 Validación de la entrega 0.2.0

Comprobaciones efectuadas el 5 de octubre de 2026 en Windows x64. Los informes privados conservan las rutas de instalación y preferencias; esta página recoge el alcance que se puede compartir.

## Paquete e instalación

- Compilación de la aplicación y del setup con cero errores, incluida una compilación del árbol de fuentes preparado para GitHub.
- Setup autoextraíble ejecutado sobre una instalación de prueba y sobre la instalación de escritorio existente.
- 142 archivos del payload verificados por tamaño y SHA-256; código fuente editable incluido.
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

## Agente instalado con su Strata activo

Se reprodujo el ejercicio de saludos sobre un archivo existente que contenía `for i in range(5): print(882)` en un proyecto de prueba propio. El agente instalado usó `read_file`, `write_file` y `finish`, preparó un programa con `input`, `while` y `print` y conservó el contenido original hasta aplicar la propuesta.

Después de aplicarla únicamente en ese proyecto de pruebas, Python verificó la estructura `while`, las entradas y los saludos con 0, 2 y 5 amigos. Se utilizó el modelo Strata activo del perfil original RTX 2060 SUPER + RX 580. Se verificó también la preparación del backend Ono_Anna en la RX 580; no se repitió síntesis ni reproducción de audio en esta prueba. Las preferencias originales se restauraron exactamente.

## Reproducir comprobaciones

Después de compilar, sobre una instalación de pruebas propia:

```powershell
$env:SHEEPCODE_HOME = 'D:\SheepCode-pruebas'
.\app\bin\Release\net8.0-windows\SheepCode.exe --self-test
.\app\bin\Release\net8.0-windows\SheepCode.exe --protocol-check
.\app\bin\Release\net8.0-windows\SheepCode.exe --workflow-check
.\app\bin\Release\net8.0-windows\SheepCode.exe --laptop-check
.\app\bin\Release\net8.0-windows\SheepCode.exe --laptop-ui-check
```

`--preflight-agent-check` usa el motor activo configurado, prepara y aplica un cambio únicamente en un proyecto de prueba propio y comprueba Python. Requiere el modelo y Python instalados. No lo uses para comparar candidatos.

## Límites de esta entrega

Windows 10/11 x64 Intel/AMD. Sin certificación de todos los escalados o controladores. Las instalaciones nuevas no incluyen el paquete neuronal de voz RX 580 original. La consulta de auditoría de vulnerabilidades de NuGet devolvió NU1900 por falta de acceso al servicio; no se declara superada. El ejecutable del setup no tiene firma Authenticode propia.
