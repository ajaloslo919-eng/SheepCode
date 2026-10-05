---
name: models
description: Consultar el perfil local, hardware, modelos, gráficas, batería y modo portátil. Usar para modelos, Strata, GPU, laptops, ahorro y configuración de IA.
---

Usa model_status para consultar la configuración y telemetría real. Distingue configurado de cargado y disponible de falló.
El perfil original de Strata usa RTX CUDA y proyecciones down Q2_0 de expertos en RX 580; su voz neuronal expresiva permanece en la RX 580. Otras instalaciones pueden tener un perfil CPU/Vulkan más pequeño o Strata CUDA/HIP.
La persona puede decir «Activa el motor», «Desactiva el motor», «Pon el razonamiento alto» y «Analiza el sistema», también por dictado, o usar el panel Modelos. system_info consulta hardware y una recomendación basada en memoria y disco, sin ejecutar modelos.
El catálogo contiene descargas con versión y SHA-256. Una recomendación no demuestra calidad ni rendimiento. Solo una petición humana explícita «Instala el modelo recomendado» instala y selecciona el perfil; el agente no tiene una herramienta para descargar ni cambiar modelos. No descargues ni compares otra IA para suplir una función ausente. No afirmes inferencia en GPU basándote solo en la configuración: consulta el estado real.

Para portátiles Windows x64 usa portable_status y system_info: consultan batería/corriente, RAM compartida y modo real. system_info vuelve a enumerar DXGI y los dispositivos presentes de Windows/PnP. Una RTX solo registrada en Windows aparece con VRAM sin verificar; exige verificación de identidad y memoria por Vulkan antes de usarla. Respeta los errores y tarjetas deshabilitadas; no inventes una GPU ausente ni la actives. La RAM de una integrada no se suma como VRAM adicional. Los perfiles llama.cpp para portátiles reservan más RAM y usan contexto 4096; Strata conserva su contexto. Con GPU híbrida se prefiere una dedicada verificada por Vulkan. Una integrada requiere proveedor Vulkan, memoria suficiente y presupuesto de RAM; si no se verifica, el perfil informa CPU.

Solo la persona configura con «Activa el modo ahorro», «Desactiva el modo ahorro» o «Modo portátil automático», también tras aceptar un dictado. Automático ahorra en batería o ahorro de Windows. En llama.cpp, ahorro carga CPU, hasta cuatro hilos y contexto máximo 4096. Los límites de GPU y contexto cambian en la siguiente carga, sin interrumpir una tarea ni cambiar o descargar modelos. El proceso propio usa prioridad de CPU reducida; Strata y la voz original RX 580 conservan sus gráficas. No prometas temperatura, autonomía o inferencia en una iGPU sin prueba física.
