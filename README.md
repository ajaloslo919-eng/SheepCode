<p align="center"><img src="docs/sheep-kuky.svg" alt="Sheep y Kuky en su pequeño taller" width="720"></p>

<h1 align="center">🐑 SheepCode · Sheep & Kuky 🐱</h1>
<p align="center">Un pequeño taller de código con IA local, ventanas, pestañas y mucho cariño 🌸</p>
<p align="center"><a href="https://github.com/ajaloslo919-eng/SheepCode/releases/latest">🌷 Descargar setup</a> · <a href="GUIA.md">📖 Guía completa</a> · <a href="LICENSE">MIT</a></p>

SheepCode es una aplicación experimental para **Windows 10/11 x64**. Abre una carpeta, pide un cambio y revisa su diff antes de aplicarlo. Incluye el código fuente editable de la aplicación y del instalador.

🌱 **Strata CPU para Celeron / 4 GB:** backend del fork con Qwen3-0.6B, CPU x64/SSE2 sin exigir AVX, contexto 4096 y memoria limitada. El setup lo recomienda en equipos pequeños o sin AVX2. Usa la biblioteca densa llama.cpp/ggml del commit fijado por Strata y conserva el backend original de dos GPU. Código y límites en [engine/strata-cpu](engine/strata-cpu/README.md). La compatibilidad de memoria e instrucciones se verifica mediante emulación; la GUI y las herramientas se prueban en Windows. No equivale a medir un Celeron físico.

## ✨ Tu taller

- Editor, explorador, chat y propuestas de cambios con aplicar, rechazar y deshacer.
- 55 skills con buscador: código, PC, navegador, documentos, hojas, PDF, presentaciones, Git, respaldos y flujos de desarrollo. Importa y crea tus propios `SKILL.md`.
- Herramientas locales para proponer DOCX/XLSX/PPTX/PDF básicos y CSV/HTML/SVG editables, con Aplicar y Deshacer.
- Conexiones MCP HTTP/stdio para imágenes, servicios externos, despliegues o Excel en vivo; muestran **sin configurar** hasta conectar un proveedor y autorizar sus herramientas.
- Actualizador integrado en **Skills → Actualizar**: busca releases, descarga y verifica SHA-256 antes de abrir el setup en tu carpeta actual.
- Automatizaciones locales de lectura y propuestas con SheepCode abierto y el motor listo.
- Pestañas HTTP/HTTPS: abrir, leer por fragmentos, rellenar controles permitidos y navegar.
- Setup decorado que analiza RAM, CPU, disco, batería y gráficas; propone un modelo local según la capacidad del equipo.
- Portátiles Intel/AMD, iGPU y equipos híbridos: inventario DXGI + Windows/PnP, validación Vulkan y modo de ahorro.
- Dictado opcional. La voz expresiva Ono_Anna usa la RX 580 cuando está configurado el paquete neuronal original.

La recomendación del modelo es una estimación de memoria y compatibilidad. No mide ni compara candidatos para demostrar cuál es el mejor. La descarga de la IA necesita Internet; el uso del modelo instalado es local. El navegador sí accede a las webs que abras.

## 🌷 Instalar

1. Descarga **SheepCode-Setup.exe** y **SHA256SUMS.txt** de [Releases](https://github.com/ajaloslo919-eng/SheepCode/releases).
2. Ejecuta el setup con una cuenta normal y elige las carpetas de la aplicación y los modelos.
3. Acepta el perfil recomendado, elige uno más ligero o instala solo el editor. Termina la descarga para usar la IA.
4. Abre una carpeta de proyecto. Escribe lo que necesitas y pulsa **Aplicar** cuando hayas revisado la propuesta.

El paquete incluye un runtime .NET privado. Prepara WebView2 con el instalador oficial si falta. Los modelos están fijados por revisión y hash; las descargas interrumpidas se pueden reanudar.

Para comprobar el ejecutable en PowerShell:

```powershell
Get-FileHash .\SheepCode-Setup.exe -Algorithm SHA256
```

El setup de esta edición no tiene firma Authenticode propia. La [guía](GUIA.md) explica actualización, respaldo, desinstalación y dependencias.

## 🧶 Modelos y portátiles

En equipos pequeños se ofrecen modelos Qwen3 ligeros mediante llama.cpp CPU/Vulkan. Una GPU integrada comparte RAM con Windows; esa memoria no se cuenta dos veces. Una RTX detectada solo por Windows/PnP aparece con VRAM sin verificar y necesita validación Vulkan antes de usarse. El setup permite volver a detectar las gráficas y desplazar la lista completa.

En **Modelos → Energía**, Automático aplica el ahorro en batería para llama.cpp. Ahorro usa CPU, hasta cuatro hilos y contexto 4096 al cargar. También puedes escribir «Estado del portátil», «Activa el modo ahorro» y «Modo portátil automático».

Strata se ofrece en equipos que cumplan sus requisitos. Se incluye soporte para reutilizar el perfil original de SheepCode con RTX 2060 SUPER + RX 580. La combinación de fabricantes de ese perfil no está certificada en otros equipos. El paquete de voz neuronal RX 580 original no se distribuye con este setup; en otros equipos la lectura de voz aparece sin configurar.

No hay versiones nativas para ARM64, macOS o Linux. El soporte de portátil se ha comprobado con fixtures y ventanas reales en un escritorio Windows; falta validarlo en un portátil físico.

## 🐾 Skills y permisos

Activa las skills desde su panel o con «Activa la skill browser». Para controlar el PC, selecciona una ventana. Las pestañas pertenecen al navegador integrado de SheepCode. Las skills personalizadas agregan instrucciones, sin ampliar permisos ni ejecutar scripts.

Las skills adaptan áreas de trabajo habituales de un agente de código. Los flujos de instrucciones, herramientas locales y proveedores externos tienen estados distintos en el catálogo. Los servicios propietarios de Codex, cuentas externas, OCR avanzado y generación de imágenes necesitan un proveedor MCP compatible; no se incluyen accesos o credenciales. [Configuración y ejemplos](GUIA.md).

El contenido de archivos y webs se trata como datos. El agente propone cambios; guardarlos exige una acción humana. Ejecutar comprobaciones del proyecto requiere habilitarlas. No se permiten comandos de terminal arbitrarios ni acceder a campos de contraseña. **Parar** cancela la petición y termina el proceso propio de IA; conserva las propuestas anteriores y la siguiente tarea vuelve a cargar el motor.

## 🛠️ Editar y compilar

Se necesita el SDK .NET 8 y Windows x64:

```powershell
git clone https://github.com/ajaloslo919-eng/SheepCode.git
cd SheepCode
.\build-source.ps1
```

El resultado queda en `app/bin/Release/net8.0-windows`. El setup instala estos mismos archivos bajo `source/`. `app/skills` contiene las skills de fábrica; `state/skills` guarda las personalizadas fuera del repositorio.

`shared/` contiene detección de hardware, perfiles y descargas; `setup/`, el asistente; `packaging/`, los lanzadores nativos y el empaquetado. Reconstruir el setup requiere Visual Studio 2022 C++, Windows SDK y las descargas fijadas por `packaging/build-release.ps1`. Consulta [GUIA.md](GUIA.md).

## 🌼 Validación

La entrega comprueba el setup autoextraíble, los hashes del paquete, los perfiles de portátil, el panel de gráficas, la GUI a cuatro tamaños, los permisos y las regresiones de contexto, propuesta y cancelación. Las pruebas programadas de HTTP no equivalen a probar una web externa ni a medir rendimiento de IA. [Detalles de validación](docs/VALIDACION.md).

## 📜 Licencias

El código de SheepCode usa [MIT](LICENSE). Las dependencias y los modelos mantienen sus propias licencias; consulta `packaging/licenses`, `app/THIRD-PARTY-NOTICES.txt` y el catálogo. Gracias a [Strata](https://github.com/Niko1221/Strata), [llama.cpp](https://github.com/ggml-org/llama.cpp), [Qwen](https://huggingface.co/Qwen), [Whisper](https://github.com/ggml-org/whisper.cpp), [Whisper.net](https://github.com/sandrohanea/whisper.net), [NAudio](https://github.com/naudio/NAudio) y [WebView2](https://learn.microsoft.com/microsoft-edge/webview2/).
