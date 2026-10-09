# 🐑 Validación de SheepCode 0.5.1

Esta versión corrige la presentación del modelo seleccionado y la reparación del runtime nativo. Se verifican archivos y herramientas de SheepCode; no se ejecutan otros modelos, candidatos ni comparativas de IA.

## Diagnóstico y reparación

Las 21 comprobaciones deterministas verifican: equipo sin perfil, catálogo sin pesos, descarga parcial y archivo de tamaño distinto; directorio de runtime ausente y causa exacta conservada; diagnóstico por el diálogo compartido y capacidades registradas; carpeta de proyecto diferenciada de pesos; comandos citados y skill desactivada; respeto de runtimes personalizados; reparación CPU y Vulkan desde los ZIP realmente distribuidos; recuperación de CRT y DLL incompletas; reparación del perfil seleccionado durante una actualización; respaldo, rechazo de SHA-256 inválido, cancelación y conservación de perfil, permisos y bytes de pesos.

Los archivos de pesos son fixtures de texto que no pueden ejecutarse. Se extraen los runtimes reales CPU/Vulkan, pero no se lanzan para inferencia, no se descargan modelos y no se cambia el backend de voz. Presencia y tamaño no prueban SHA-256 de los pesos ni que el modelo esté cargado; `engine.ready` informa del proceso activo.

La GUI se comprueba a 1024 × 650 y 1520 × 900 en una instalación aislada sin IA seleccionada: el resumen está arriba, muestra el fallo y no repite el catálogo. El modelo elegido y la inferencia del dispositivo remoto del usuario todavía requieren comprobarse en ese equipo.

## Regresiones

Se verifican las 10 comprobaciones de herramientas de proyecto y permisos, y las tres regresiones de flujo con respuestas HTTP deterministas (contexto 4096, lectura previa de archivos y cancelación real del proceso propio y HTTP). Los fixtures no son inferencia ni una medida de rendimiento de SheepCode.

La distribución conserva el código editable y los paquetes fijados. Las evidencias de imágenes, visión/3D, validación de código y límites de la versión anterior están en [VALIDACION-0.5.0.md](VALIDACION-0.5.0.md); no se presentan como pruebas nuevas. La voz neuronal RX 580 no se sustituye ni se declara nuevamente verificada en este arreglo.
