# ⚡ Validación de SheepCode 0.4.1 · Qwen rápido

Trabajo del 6 de octubre de 2026. Se ejecuta exclusivamente Qwen3-0.6B Q8_0 seleccionado en la instalación propia de SheepCode. Los pesos conservan su SHA-256; no se ejecutan modelos candidatos ni otra IA.

## Velocidad del flujo instalado

En el mismo PC de desarrollo Windows x64, Intel i5-12400F, RAM 64 GiB y perfil CPU de dos hilos, la creación de hello.py con una línea solicitada pasó de **94,06 s a 24,88 s**, aproximadamente **3,8 veces más rápida en esa comprobación**. Se incluyeron arranque del motor, inferencias, propuesta, aplicación humana del arnés y verificación del archivo. El motor usa CPU, no las GPU del equipo. Ambas versiones usan los mismos pesos Qwen3-0.6B; el resultado no predice la velocidad de un Celeron físico ni de tareas diferentes.

La versión optimizada completó write_file y finish sin reintentos: 339 tokens en la primera entrada y 62 de salida. El siguiente paso conservó 401 tokens en caché y evaluó 58 nuevos. Su prefill fue 2,06 s; la generación, 4,95 s. Estos tiempos son del **componente**, y excluyen herramientas, GUI y voz. La respuesta final de SheepCode informó correctamente que el cambio aún requería revisión, aunque el modelo describiera el archivo como creado.

## Cambios integrados

- Producto Q8 firmado mediante SSE2, sin AVX, duplicación de pesos ni cambio de cuantización. Al cargar, se verifica contra una suma escalar, incluidos -128, escalas y bloques impares.
- Conserva el prefijo no pensante del asistente entre pasos para reutilizar también sus tokens generados.
- Conteo exacto con /prompt-count; valida el contexto antes de inferir y conserva reserva para la salida.
- Gramática de acciones generada desde el registro de herramientas: campos requeridos al nivel superior. La validación, permisos y revisión humana siguen en SheepCode.
- Modo rápido activado por defecto: instrucciones breves, las 55 skills disponibles a demanda, rutas relativas, prelectura del archivo abierto mencionado y salida inicial de 512 tokens con reintentos acotados hasta 1536.
- Estado y configuración disponibles en capabilities, performance_status, la casilla ⚡ Qwen rápido y las órdenes humanas compartidas por texto y dictado aceptado. La skill models desactivada bloquea su configuración.
- El setup actualiza el runtime estándar Strata CPU incluso al actualizar solo app/source: verifica paquete y ejecutable, respalda el anterior y conserva perfil, pesos y voz. Respeta runtimes con ruta personalizada.

## CPU antigua y memoria

El código actualizado completa una respuesta JSON bajo QEMU TCG, CPU Conroe/Celeron 4x0, dos CPU virtuales, **4096 MiB y sin AVX**, usando el modelo seleccionado en SheepCode. La salud confirma cálculo SSE2 verificado, contexto 4096, dos hilos y presupuesto 1536 MiB. Memoria residente observada **1198 MiB**, espacio virtual **1398 MiB** y aproximadamente **2580 MiB disponibles** en el invitado, sin swap. Esta prueba verifica el componente y compatibilidad de memoria/instrucciones; no mide velocidad física ni incluye una GUI Windows en la VM.

Windows limita memoria privada con un Job Object; Linux limita espacio virtual. Las páginas mapeadas no se contabilizan igual. Windows, GUI, herramientas y pestañas consumen RAM adicional.

## Comprobaciones y límites

Las comprobaciones de flujo controlado separan respuestas simuladas de inferencias reales. Verifican prelectura antes de inferir, conservación de una edición concurrente, propuestas sin guardar, activación/desactivación humana, citas literales y bloqueo por skill desactivada. El componente real verifica conteo exacto, caché del asistente, contexto excesivo, cancelación nativa y detención de su proceso propio.

El programa de saludos con while **no está verificado en esta versión**: la primera inferencia excedió el límite de tres minutos de esa comprobación y se detuvo su proceso de prueba. No se aplicó ni ejecutó el programa ni se presenta el XLSX posterior como un éxito. La prueba 0.4.0 tampoco pasó ese programa; su informe queda en VALIDACION-0.4.0.md. Acelerar este modelo básico no garantiza que resuelva tareas complejas: revisar el diff y ejecutar comprobaciones autorizadas.

El perfil original de dos GPU y su voz neuronal expresiva RX 580 conservan archivos, ajustes e identidad; no se atribuye una nueva síntesis a esta comprobación. El setup mantiene fuentes editables y actualización por SHA-256. No tiene firma Authenticode propia. Las pruebas históricas del modelo grande permanecen separadas en VALIDACION-0.3.0.md.
