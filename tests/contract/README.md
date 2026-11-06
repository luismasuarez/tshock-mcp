# Contrato de `tools/list`

`tools-list.snapshot.json` es el registro generado del catálogo que un cliente MCP recibe por
stdio. Congela los nombres, títulos, descripciones, `inputSchema`, `annotations` y el orden de las
23 herramientas. Es un artefacto de prueba, no documentación escrita a mano: se obtiene del binario
Release y se compara en cada ejecución de la e2e.

## Qué incluye y qué excluye

El snapshot guarda solo el catálogo. No incluye `resultType`, `ttlMs`, `cacheScope`, `_meta`,
`serverInfo` ni la versión del ensamblado. Esos campos pertenecen al sobre de `tools/list` y a
`server/discover`; congelarlos aquí acoplaría este contrato con P2-16 y P2-17 y produciría fallos
falsos ante cambios que no afectan a las herramientas.

El formato es:

```json
{
  "formatVersion": 1,
  "order": ["events_since", "..."],
  "tools": {
    "events_since": {
      "name": "events_since",
      "title": "Get server events",
      "description": "...",
      "inputSchema": {},
      "annotations": {}
    }
  }
}
```

`order` y `tools` se comparan por separado. Así un cambio de descripción es un diff de una línea y
un reordenamiento del catálogo es otro diff distinto, en vez de 23 bloques reordenados.

## Procedencia

El archivo se genera desde el binario real, no desde `docs/tools.md` ni desde objetos del SDK. La
captura abre tres procesos y completa el handshake de cada era:

- `2025-06-18`: `initialize` + `notifications/initialized`.
- `2025-11-25`: la misma ruta legacy con la revisión intermedia.
- `2026-07-28`: `server/discover` + `_meta` por petición.

Si las tres eras no emiten el mismo catálogo, la regeneración falla. También sigue `nextCursor` si
el servidor paginara la lista.

## Comandos

```bash
dotnet build -c Release

# Verificar el contrato (modo por defecto; también acepta --check)
python3 tests/contract/tools_list_contract.py --check

# Probar que el comparador detecta cada clase de mutación
python3 tests/contract/tools_list_contract.py --self-test

# Regenerar el snapshot de forma intencional
python3 tests/contract/tools_list_contract.py --write
```

`--write` se rechaza cuando `CI` o `GITHUB_ACTIONS` están definidos: la actualización es una
decisión revisable, no un efecto secundario de la integración continua.

## Política de cambios

- **Aditivo:** una herramienta nueva, un campo opcional nuevo o una aclaración de descripción. Se
  actualiza el snapshot en el mismo commit y el diff lo muestra.
- **Ruptura:** renombrar o quitar una herramienta, cambiar el tipo o el límite de un argumento,
  cambiar un hint o reordenar el catálogo sin actualizar el snapshot. La e2e falla con la ruta del
  campo y el diff; el cambio exige revisión explícita.
- El orden es contrato observable. P1-07 puede cambiarlo al repartir las tools por dominio; en ese
  commit se actualiza el snapshot y el diff del orden queda como evidencia.

Antes de escribir, `--write` valida invariantes estructurales: nombres únicos, `inputSchema` de
tipo `object`, `required` contenido en `properties`, los cuatro hints booleanos, título no vacío y
distinto del `name`, y descripción no vacía. Una regeneración no puede bendecir un catálogo roto.

## Dónde se ejecuta

`tests/e2e/mcp_stdio_smoke.py` ejecuta el self-test y compara el snapshot en:

- cliente legacy `2025-06-18`;
- cliente `2025-11-25` con elicitation;
- cliente moderno `2026-07-28`;
- escritura habilitada y deshabilitada;
- puente configurado, con escritura y detenido.

Todas las variantes deben emitir exactamente el mismo catálogo. Además, dos procesos independientes
deben producir el mismo catálogo normalizado; si el orden dependiera de la reflexión o del
ensamblado, la prueba lo detectaría.

## Verificación independiente

La comprobación de aceptación es la e2e. Para una validación externa puntual, el Inspector oficial
de MCP puede listar el catálogo del mismo binario:

```bash
TSHOCK_REST_URL=http://127.0.0.1:9 \
TSHOCK_TOKEN=contract-capture-token \
npx @modelcontextprotocol/inspector dotnet \
  src/TShock.MCP.Server/bin/Release/net8.0/tshock-mcp.dll
```

`tools/list` no llama a TShock ni al puente, así que basta un token ficticio y una URL de loopback
cerrada. No se usan secretos reales en esta prueba.
