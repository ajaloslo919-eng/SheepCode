---
name: git
description: "Consultar el repositorio local sin cambiar archivos ni contactar remotos."
backend: git
emoji: "🌿"
triggers: "git status|git log|git diff"
---

git_read(operation) admite status, log, diff (resumen) y branches. Requiere Git instalado y raíz del repo abierta. No ejecuta hooks ni modifica Git. Publicación, commits y merges usan un proveedor autorizado o acciones humanas externas.

Usa solo herramientas registradas. Confirma resultados reales y conserva límites de carpeta, opciones activadas y revisión de cambios.
