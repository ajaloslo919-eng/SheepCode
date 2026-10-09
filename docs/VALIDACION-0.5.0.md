# 🐑 Validación de SheepCode 0.5.0

Esta release reúne revisión de código, adjuntos/OCR, visión local, inspección 3D y creación local de imágenes. Las verificaciones usan exclusivamente componentes seleccionados e instalados de SheepCode. Los fixtures de modelos, HTTP o hardware son deterministas y se distinguen de inferencia real; no se hacen comparativas con otras IAs.

## Creación de imágenes y contexto

El 8 de octubre se verificó en la instalación de Windows la entrada exacta «crea la imagen de un gato». SD-Turbo Q8 produjo un PNG real de 512 × 512, inspeccionado visualmente y verificado por SHA-256. El componente tardó 8,05 segundos en la RTX 2060 SUPER con texto auxiliar CPU. El agente no llamó al motor de código ni MCP; no alteró propuestas, opciones o permisos. Este tiempo no representa el rendimiento de escritura de código, voz o un Celeron físico.

La integración se probó antes de cambiar el número de distribución a 0.5.0. También se verificaron 31 casos deterministas de variantes naturales, negaciones, citas, descripción ausente, generación/skill desactivada y reserva de contexto del perfil instalado Strata dual de 8192 tokens. Las mismas rutas sirven para texto y dictado aceptado. El contexto inicial no incorpora estados completos de vistas previas/adjuntos; carga una skill pertinente y mantiene las políticas de permisos y revisión.

Tres regresiones del flujo con respuestas simuladas verificaron: lectura por fragmentos de una página larga en 4096 tokens y recuperación del HTTP 400 sin repetir la navegación; lectura previa del archivo Python y propuesta sin sobrescribir; botón Parar que cancela HTTP y el proceso auxiliar propio. Cuatro regresiones HTTP/JSON comprobaron reintentos acotados, content obligatorio y campos al nivel superior.

## Otras funciones integradas

Las comprobaciones del 7 de octubre separadas de la generación cubrieron 28 casos de adjuntos/OCR y permisos, 20 de visión/3D (componente SmolVLM 500M instalado y Blender), cuatro tamaños de GUI para visión/3D y cuatro para el generador, y 48 de revisión sintáctica/requisitos explícitos. Las respuestas del motor de código en las pruebas del agente son simuladas; la revisión Python usa AST y no ejecuta el código candidato. No se infiere rendimiento o calidad de los modelos opcionales de esos fixtures.

Granite 4.0 H350M y H1B (1.5B) son alternativas manuales experimentales; el catálogo no promete que completen una tarea. Qwen2.5 Coder sigue siendo una opción manual. Las pruebas históricas del perfil Qwen se conservan en [VALIDACION-0.4.1.md](VALIDACION-0.4.1.md), con sus límites y fallos explícitos.

## Distribución y límites

El setup incluye runtime privado de .NET, editor, 57 skills locales/adaptadas, analizadores y código editable. El modelo de código elegido se descarga por revisión y SHA-256; visión y generación se instalan mediante controles humanos. SD-Turbo requiere unos 2,02 GB de pesos y al menos 8 GB de RAM. La visión es aproximada; FBX/BLEND necesitan Blender instalado y solo muestran geometría base; Unity YAML no ejecuta el juego. El generador no garantiza texto, transparencia, anatomía ni cantidades.

La actualización respaldó la app y el estado de la instalación usada para la integración. Se verificaron los hashes de preferencias, modelos/configuración, componentes visuales y TTS; la búsqueda automática solo renovó su caché al reabrir. La voz neuronal original de la RX 580 conserva configuración e identidad; esta release no atribuye una nueva prueba de síntesis de voz. Las instalaciones sin ese paquete informan «sin configurar».

Los modelos, archivos, webs y skills no pueden ampliar permisos ni aplicar propuestas. Guardar PNG y aplicar código siguen siendo decisiones humanas. Los paquetes tienen manifiestos de integridad; el setup no tiene firma Authenticode propia. Las licencias de pesos y dependencias se incluyen aparte de la licencia del código.

## Paquete 0.5.0

El setup nativo compiló sin advertencias ni errores y realizó una instalación limpia aislada, sin descargar ni ejecutar modelos. Se verificaron su versión, el binario instalado, 57 skills, 177 archivos de código editable y el Python privado de revisión. Pasaron 17 comprobaciones del setup, 10 de la app, 18 del catálogo/artefactos/MCP/actualizador, 48 de revisión, 31 de rutas de generación/permisos/contexto, cuatro de protocolo y tres del flujo con servidor/tokenizador simulados. El perfil del transporte simulado está separado del perfil instalado y no puede iniciar motores.
