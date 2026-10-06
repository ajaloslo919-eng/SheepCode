# 🌱 Validación de SheepCode 0.4.0 · Strata CPU

Trabajo del 6 de octubre de 2026. Se ejecuta exclusivamente el modelo seleccionado en la instalación propia de SheepCode y sus componentes integrados. No se comparan modelos ni se sustituyen resultados por otra IA.

## Compatibilidad de CPU y memoria del motor

El fork de Strata añade STRATA_LOW_MEMORY_CPU. Se compila desde el árbol original de Strata y desde la carpeta editable engine/strata-cpu. El backend GPU/MoE y los cambios locales RX 580 se conservan. El backend denso usa la biblioteca MIT llama.cpp/ggml del mismo commit fijado por Strata; no ejecuta llama-server ni su servidor Python.

Modelo activo: Qwen3-0.6B Q8_0 oficial, revisión y SHA-256 fijados en el catálogo. La comprobación arranca un kernel Linux en QEMU TCG con **4096 MiB, dos CPU Conroe/Celeron 4x0 emuladas y sin AVX**. Usa el mismo código del backend y el modelo seleccionado en SheepCode. El sistema registra 4.014.680 KiB de RAM utilizable y cero swap. La salud confirma dense-cpu, contexto 4096, dos hilos, límite 1536 MiB y AVX/AVX2 compilados a cero.

Completa una respuesta JSON: 36 tokens de entrada y 10 de salida, terminada con stop. Memoria residente observada aproximadamente **1197 MiB**; espacio virtual aproximadamente **1398 MiB**. El invitado conserva aproximadamente **2583 MiB disponibles** después de generar. Los registros conservan CPU, meminfo y estado del proceso. Esto verifica instrucciones y memoria del **componente**; no mide la velocidad de un Celeron físico ni una GUI Windows dentro de la VM.

En Windows el proceso propio aplica un Job Object con límite de memoria privada; Linux limita espacio virtual. Estos límites difieren: no todas las páginas mapeadas se incluyen en la memoria privada de Windows. La telemetría separa residencia, pico y memoria privada. Windows, la GUI, Python de comprobaciones y las pestañas consumen memoria adicional. Requiere CPU x64; no promete cualquier Celeron de 32 bits o un equipo cuya RAM está ocupada.

## Flujo instalado y controles

- Setup real instala y selecciona Strata CPU, pesos y ejecutable verificados, y fuentes editables. No necesita GPU ni Python para inferencia.
- El agente Windows crea hello.py con el contenido solicitado como propuesta, sin guardarlo antes de Aplicar. La petición humana del arnés aplica el cambio y se comprueba el archivo real.
- Activar, consultar estado, ajustar ahorro, detener el motor y desactivar ahorro funcionan por la entrada compartida de texto y dictado aceptado. No se afirma reconocimiento de voz dentro de la VM.
- Rechaza una entrada excesiva antes de generar. La cancelación nativa interrumpe una petición activa en aproximadamente **0,047 s** en el equipo de desarrollo, y después se detiene su proceso propio. Esa latencia no se atribuye al Celeron emulado o físico.
- Pruebas instaladas de propuestas, protocolo, contexto 4096, recuperación HTTP, botón Parar con transporte controlado, portátiles, catálogo, actualizador y ventanas de 800–1520 px. Los fixtures están separados de las llamadas reales a Strata CPU.

## Límites observados

El modelo 0,6B es básico. La prueba de reemplazar el programa por un saludo con while **no pasó**: produjo propuestas con errores de formato o lógica. No se presenta ese ejemplo ni el XLSX posterior como un éxito de este perfil CPU. Las instrucciones reducidas, prelectura, rechazo de propuestas sin efecto y comprobación de campos evitan algunos fallos de protocolo; no garantizan corrección semántica. Revisar el diff y ejecutar pruebas autorizadas. El motor sí carga, genera y usa herramientas dentro de los límites verificados.

La voz neuronal expresiva original RX 580 mantiene sus archivos, perfil e identidad. No se cambió ese backend; en instalaciones sin su paquete figura sin configurar. NuGet no pudo consultar la auditoría de vulnerabilidades (NU1900); la compilación no tuvo errores. El setup no tiene firma Authenticode propia.

La validación 0.3.0 queda archivada en VALIDACION-0.3.0.md; su éxito con el modelo grande no se atribuye al modelo pequeño.
