# 🌱 Strata CPU de SheepCode

Backend añadido al fork de Strata para CPU x64, incluso sin AVX/AVX2, y equipos con 4 GB de RAM. El backend GPU/MoE original conserva sus rutas y pesos. Este backend usa la biblioteca MIT `llama.cpp/ggml` del mismo commit fijado por Strata (`3cf03257f219afbe7334045ff7c6a06ac68c627d`) para el grafo denso, vocabulario y gramática. No ejecuta `llama-server`, no carga el servidor Python y no pretende ejecutar el modelo MoE de 70 GB en 4 GB.

Modelo integrado: Qwen3-0.6B Q8_0, oficial de Qwen, Apache-2.0, SHA-256 fijado en `shared/model-catalog.json`. Solo texto, contexto 4096, una generación concurrente, hasta 2 hilos en el perfil automático. El razonamiento oculto se desactiva. El modelo es pequeño: prepara cambios cortos y revisa su código.

El proceso limita memoria, mapea los pesos sin bloquear páginas ni duplicarlos para repacking, mantiene KV en FP16 y procesa la entrada con bloques lógicos de 64 y físicos de 32 tokens. Reutiliza el prefijo validado de una conversación. El presupuesto de proceso predeterminado es 1536 MiB; Windows limita memoria privada comprometida y Linux limita espacio virtual. **Estos límites difieren**: el límite Windows no incluye todas las páginas mapeadas del archivo. `/health` informa memoria residente, pico y memoria privada cuando el sistema la ofrece; no confundirlo con la RAM de todo el equipo.

API privada en `127.0.0.1`: `/health`, `/v1/models`, `/apply-template`, `/tokenize`, `/v1/chat/completions`, `/cancel`. Cuenta tokens reales antes de admitir una entrada y reserva su salida. La salida JSON aplica gramática. Una petición incompleta no se ejecuta como herramienta. Cancelar detecta desconexión y comprueba el callback de CPU; SheepCode también puede terminar únicamente su proceso propio.

Compilación independiente con CMake 3.24+, C++17 y Ninja:

```sh
cmake -S engine/strata-cpu -B engine/strata-cpu/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build engine/strata-cpu/build --target strata-cpu --parallel 2
```

`STRATA_GGML_DIR` permite usar un checkout local del commit indicado. En Windows, `packaging/build-strata-cpu.ps1` prepara un ZIP y hashes para el setup, con Visual Studio 2022 C++ y Windows SDK. Compila con instrucciones x64/SSE2, sin CUDA/HIP/Vulkan/AVX/OpenMP. En Linux, `STRATA_CPU_STATIC_LINUX=ON` permite compilar el binario para la prueba de compatibilidad aislada.

Integración en el árbol original de Strata: copiar `cmake/StrataLowMemory.cmake` y `src/cpu/server.cpp`, y activar el bloque de `upstream-integration.patch`; configurar `-DSTRATA_LOW_MEMORY_CPU=ON`. La opción retorna antes de preparar el backend GPU. Los cambios locales RX 580 se conservan.

El setup selecciona este perfil con menos de 8 GB o sin AVX2. También se puede elegir explícitamente. Modelos y estado permiten comprobar, activar, desactivar y restaurar el perfil anterior mediante entrada humana de texto o dictado aceptado; el modelo no puede otorgarse permisos ni instalar pesos. En equipos sin el paquete y adaptador RX 580, esa voz figura como sin configurar. La voz neuronal original conserva su backend e identidad.
