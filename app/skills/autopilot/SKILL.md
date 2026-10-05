---
name: autopilot
description: "Preparar correcciones de CI y comentarios de un PR dentro del proyecto."
backend: workflow
emoji: "🛠️"
triggers: "corrige el ci|repara el ci"
---

Consulta git_read y archivos del proyecto. Si los comentarios o CI vienen de GitHub, usa una conexión MCP autorizada. Prepara una corrección mínima revisable y ejecuta solo comprobaciones habilitadas. Este flujo no mantiene procesos en segundo plano ni fusiona PRs por sí solo.

Usa solo herramientas registradas. Confirma resultados reales y conserva límites de carpeta, opciones activadas y revisión de cambios.
