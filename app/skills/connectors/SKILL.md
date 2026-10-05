---
name: connectors
description: "Descubrir y usar servidores MCP configurados por la persona."
backend: mcp
emoji: "🔗"
triggers: "conexiones mcp|herramientas mcp"
---

mcp_status muestra conexiones y permisos; mcp_tools(server) descubre schemas reales. mcp_call(server,tool,arguments,skill) usa arguments como cadena JSON con objeto. Solo nombres exactos autorizados, skill activada y aprobación humana de esa llamada. La configuración se guarda desde Conexiones; tokens son variables de entorno.

Usa solo herramientas registradas. Confirma resultados reales y conserva límites de carpeta, opciones activadas y revisión de cambios.
