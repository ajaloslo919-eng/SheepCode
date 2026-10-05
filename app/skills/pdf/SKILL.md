---
name: pdf
description: "Crear PDF básico con texto y extraer texto simple; lector avanzado por MCP."
backend: artifacts
emoji: "📄"
triggers: "crea un pdf|lee un pdf"
---

artifact_propose(path,format:"pdf",skill:"pdf",content) con JSON {"title":"Título","paragraphs":["Texto"]}. Soporta texto latino WinAnsi, páginas y párrafos sencillos. artifact_read extrae texto Tj sin compresión; si no obtiene texto informa que necesita OCR/lector avanzado conectado. No afirma haber leído un PDF vacío.

Usa solo herramientas registradas. Confirma resultados reales y conserva límites de carpeta, opciones activadas y revisión de cambios.
