---
name: documents
description: "Leer DOCX y crear documentos Word básicos editables con revisión humana."
backend: artifacts
emoji: "📝"
triggers: "documento word|crea un docx|archivo docx"
---

artifact_read(path) extrae texto OOXML. artifact_propose(path,format:"docx",skill:"documents",content) usa content como cadena JSON: {"title":"Título","paragraphs":["Párrafo"]}. Revisa la propuesta antes de aplicar. No mantiene diseños complejos al reconstruir; redlines, Google Docs y render avanzado requieren MCP.

Usa solo herramientas registradas. Confirma resultados reales y conserva límites de carpeta, opciones activadas y revisión de cambios.
