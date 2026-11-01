# Pruebas y criterios de aceptación

Este documento define cómo se prueba `tshock-mcp` y qué se considera terminado. Es la referencia
para cualquier agente o colaborador: no se inventan procedimientos ad hoc.

## Principio

La prueba de aceptación de una funcionalidad del gateway es la **capa MCP**: el binario real, el
protocolo JSON-RPC por stdio y la herramienta real. Las capas inferiores sirven para aislar fallos,
pero no cierran una funcionalidad.

Una prueba con `curl` al puente o a la API REST es diagnóstico. No demuestra que un agente pueda
usar la funcionalidad.

## Capas

| Capa | Qué cubre | Cómo se ejecuta | Red | ¿Es aceptación? |
| --- | --- | --- | --- | --- |
| Unidad | Lógica pura: validación, sobre, redacción, ring buffer. | `dotnet test -c Release` | Ninguna | No |
| Plugin HTTP | Contrato del puente: rutas, cursores, errores, regiones. | Script contra `127.0.0.1:7879` con el token del puente | Loopback | No |
| Gateway MCP | Una tool real por stdio. Fase 0: las 18 tools REST contra un TShock falso. | `python3 tests/e2e/mcp_stdio_smoke.py` | TShock falso en puerto efímero | Sí |
| Gateway MCP del puente | `events_since` y `regions_*` por stdio. | Arnés MCP con un puente falso | Loopback | Sí, cuando exista |
| Servidor real | Integración completa: TShock, plugin, gateway y cliente MCP. | Cliente MCP con secretos desde archivo | LAN o loopback | Sí, cuando la feature toca TShock |

## Comandos

```bash
# Compilación y pruebas unitarias
dotnet build -c Release
dotnet test -c Release --no-build

# Aceptación MCP de la Fase 0 (TShock falso, binario real, stdio)
python3 tests/e2e/mcp_stdio_smoke.py
```

Resultado esperado hoy: 0 advertencias, 132 pruebas superadas y `E2E PASSED` con 17 comprobaciones.

### Plugin del puente

```bash
dotnet build src/TShock.AgentBridge -c Release

cp src/TShock.AgentBridge/bin/Release/net9.0/TShock.AgentBridge.dll \
   "$HOME/.local/share/tshock-mcp/plugins/"

docker restart tshock-test
docker logs tshock-test | grep AgentBridge   # "initiated."
```

La prueba HTTP directa del plugin, con sus comandos y resultados esperados, está en
[`local-testing.md`](local-testing.md#11-probar-el-plugin-por-http-capa-inferior). Es una prueba de
capa inferior: no se usa como aceptación de una tool MCP.

### Aceptación MCP del puente

Todavía **no existe**. Cuando se implementen `BridgeClient` y `BridgeTools`, el arnés debe:

1. Levantar un puente falso en un puerto efímero.
2. Arrancar el binario real de `tshock-mcp`.
3. Completar el handshake MCP por stdio.
4. Invocar `events_since` y las tools de región.
5. Verificar el sobre, los cursores, la paginación, los errores y la degradación cuando el puente
   no responde.

La variante contra el servidor real repite las mismas llamadas con el plugin cargado y un jugador
conectado. No se acepta una feature del puente solo por haberla probado con `curl`.

## Definición de terminado

### Tool REST

- [ ] La ruta existe en la API REST de TShock y está permitida por la fase.
- [ ] Los argumentos se validan antes de la red y devuelven `invalid_argument`.
- [ ] Devuelve el sobre estable mediante `ToolResults`.
- [ ] Si escribe, comprueba `TSHOCK_MCP_ALLOW_WRITE` y devuelve `write_disabled`.
- [ ] Está documentada en `docs/tools.md` y en la tabla resumida del `README.md`.
- [ ] Tiene pruebas unitarias en `tests/TShock.MCP.Server.Tests`.
- [ ] `tests/e2e/mcp_stdio_smoke.py` la invoca por MCP y verifica el resultado.

### Tool del puente

- [ ] La ruta existe en el plugin y está documentada en `docs/bridge.md`.
- [ ] La llamada pasa por `BridgeClient`, no por un `HttpClient` ad hoc.
- [ ] El token del puente no se registra ni se devuelve.
- [ ] Los errores se traducen a códigos estables (`bridge_unavailable`, `bridge_timeout`,
      `bridge_auth_error`, `bridge_error`, `cursor_expired`).
- [ ] Si escribe, comprueba `TSHOCK_MCP_ALLOW_WRITE`; las operaciones destructivas exigen
      confirmación.
- [ ] Está documentada en `docs/tools.md`, `docs/bridge.md` y el `README.md`.
- [ ] Tiene pruebas unitarias con un manejador HTTP falso.
- [ ] Tiene una prueba MCP por stdio con un puente falso.
- [ ] Se verificó contra el servidor real llamando a la tool, no con `curl`.

## Manejo de secretos en pruebas

- Los tokens se leen de archivos con permisos `600` o de variables inyectadas por el envoltorio.
- Nunca se imprimen, ni siquiera en un mensaje de error o en un log de diagnóstico.
- No se pasan como argumento literal en la línea de comandos: quedan en el historial y en la lista
  de procesos.
- Para `curl`, use un archivo de configuración temporal con permisos `600` y bórrelo al terminar.
- Los servidores falsos usan tokens efímeros generados por la propia prueba.
- CI no recibe secretos reales: solo ejecuta la capa MCP con servidores falsos.

## Estado actual por capa

| Pieza | Unidad | Plugin HTTP | MCP stdio | Servidor real |
| --- | --- | --- | --- | --- |
| 18 tools REST de Fase 0 | Sí | No aplica | Sí: catálogo y casos representativos (17 checks) | Sí |
| Regiones del puente | No | Verificado manualmente | No existe tool | Parcial: crear, leer y borrar |
| Eventos del puente | Sí (`EventBuffer`) | Verificado manualmente | No existe tool | Parcial: región, entrada y comando |
| `BridgeClient` | No existe | No aplica | No existe | No |
| `events_since` | No existe | No aplica | No existe | No |
| Tools de región | No existen | No aplica | No existen | No |

Esta tabla debe actualizarse en el mismo cambio que la funcionalidad. Si una fila dice «No existe»,
ningún documento puede presentar esa pieza como disponible.

## Qué no es una prueba de aceptación

- Que el proyecto compile.
- Que las pruebas unitarias pasen.
- Que `curl` devuelva `200` contra el puente.
- Que la e2e de Fase 0 pase: cubre las tools REST, no el puente.
- Una prueba manual sin comando reproducible.
