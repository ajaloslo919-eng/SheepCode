---
name: spreadsheets
description: "Crear XLSX/CSV editables y leer contenido de hojas."
backend: artifacts
emoji: "📊"
triggers: "excel|hoja de cálculo|crea un xlsx|crea un csv"
---

artifact_propose(path,format:"xlsx",skill:"spreadsheets",content) con JSON {"sheet":"Gastos","rows":[["Concepto","Importe"],["Luz",30]]}. Hasta 2000 filas y 100 columnas; guarda números y booleanos como tipos reales, textos que empiezan por = como texto. CSV usa content textual. No recalcula fórmulas; análisis/formato avanzado requiere MCP.

Usa solo herramientas registradas. Confirma resultados reales y conserva límites de carpeta, opciones activadas y revisión de cambios.
