---
name: presentations
description: "Leer PPTX y crear diapositivas básicas editables."
backend: artifacts
emoji: "🎞️"
triggers: "presentación|diapositivas|crea un pptx"
---

artifact_propose(path,format:"pptx",skill:"presentations",content) con JSON {"title":"Charla","slides":[{"title":"Idea","bullets":["Punto"]}]}. Máximo 40 slides, 8 puntos por slide; títulos breves. Usa artifact_read para inspeccionar. Diagramas, notas, edición fina y Google Slides requieren proveedor avanzado.

Usa solo herramientas registradas. Confirma resultados reales y conserva límites de carpeta, opciones activadas y revisión de cambios.
