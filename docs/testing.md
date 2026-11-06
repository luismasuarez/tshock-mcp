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
| Gateway MCP del puente | Las 5 tools del puente por stdio contra un puente falso, incluida la confirmación por elicitation. | `python3 tests/e2e/mcp_stdio_smoke.py` | Puente falso en puerto efímero | Sí |
| Servidor real | Integración completa: TShock, plugin, gateway y cliente MCP. | Cliente MCP con secretos desde archivo | LAN o loopback | Sí, cuando la feature toca TShock |

### Límites de argumentos

La suite unitaria cubre, por cada argumento de texto, `max-1`, `max`, `max+1`, vacío, CRLF, NUL,
ANSI, bidi y zero-width; comprueba el conteo por code points y que no se hizo ninguna petición.
`ArgumentLimitContractTests` recorre por reflexión todas las `[McpServerTool]` y falla si un
parámetro `string` no declara `[MaxLength]`. La e2e comprueba el rechazo por MCP, que el contador de
peticiones del TShock/puente falso no crece, que el payload no aparece en `stderr` y que
`tools/list` publica `maxLength`.

### Títulos de las tools

`ToolTitleTests` recorre el catálogo registrado por ensamblado y compara los 23 títulos con una
tabla independiente. Falla si un título falta, se duplica, coincide con el `name`, supera 40
caracteres, usa un carácter fuera de la política o no coincide con `annotations.title`. La e2e
comprueba los títulos en un cliente legacy (2025-06-18) y en uno moderno (`server/discover` +
`_meta` 2026-07-28), y que ambos reciban el mismo valor.

## Comandos

```bash
# Compilación y pruebas unitarias
dotnet build -c Release
dotnet test -c Release --no-build

# Aceptación MCP de la Fase 0 (TShock falso, binario real, stdio)
python3 tests/e2e/mcp_stdio_smoke.py
```

Resultado esperado hoy: 0 advertencias, 291 pruebas superadas y `E2E PASSED` con 86 comprobaciones.

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

Existe para las tools del puente y forma parte de `tests/e2e/mcp_stdio_smoke.py`. El arnés:

1. Levanta un puente falso en un puerto efímero con un token generado por la prueba.
2. Arranca el binario real de `tshock-mcp`.
3. Completa el handshake MCP por stdio.
4. Invoca `events_since` con y sin cursor, con el límite máximo y con argumentos inválidos; invoca
   `regions_list` y `region_info` con un nombre existente, uno inexistente y uno vacío.
5. Con escritura habilitada: crea una región, repite el nombre, pide un propietario no permitido,
   borra con y sin confirmación y vuelve a borrar.
6. Se declara como cliente con capacidad de elicitation y responde a la pregunta del servidor:
   rechazarla deja la región, aceptarla la borra.
7. Verifica el sobre, la exclusividad del cursor, `hasMore`, `cursor_expired` con `details`,
   `region_not_found`, `region_exists`, `owner_not_allowed`, `confirmation_required`,
   `confirmation_declined`, que los argumentos inválidos no llegan al puente, que todo
   `error.code` observado pertenece a la tabla canónica de
   [`architecture.md`](architecture.md#mapeo-de-errores) y que el token no aparece en `stderr`.
6. Apaga el puente y comprueba que `events_since` degrada a `bridge_unavailable` mientras las tools
   REST siguen funcionando.

Cuando existan las tools de escritura de región, se agregan al mismo arnés. La variante contra el servidor real
repite las mismas llamadas con el plugin cargado y un jugador conectado. No se acepta una feature del
puente solo por haberla probado con `curl`.

## Definición de terminado

### Tool REST

- [ ] La ruta existe en la API REST de TShock y está permitida por la fase.
- [ ] Los argumentos se validan antes de la red y devuelven `invalid_argument`.
- [ ] Todo argumento `string` declara `[MaxLength]` y el esquema publica `maxLength`.
- [ ] Tiene pruebas de frontera y de caracteres de control, y la e2e comprueba que no hay petición.
- [ ] Devuelve el sobre estable mediante `ToolResults`.
- [ ] Si escribe, comprueba `TSHOCK_MCP_ALLOW_WRITE` y devuelve `write_disabled`.
- [ ] Está documentada en `docs/tools.md` y en la tabla resumida del `README.md`.
- [ ] Tiene pruebas unitarias en `tests/TShock.MCP.Server.Tests`.
- [ ] `tests/e2e/mcp_stdio_smoke.py` la invoca por MCP y verifica el resultado.

### Tool del puente

- [ ] La ruta existe en el plugin y está documentada en `docs/bridge.md`.
- [ ] Los argumentos de texto declaran su límite y lo validan antes de la red.
- [ ] La llamada pasa por `BridgeClient`, no por un `HttpClient` ad hoc.
- [ ] El token del puente no se registra ni se devuelve.
- [ ] Los errores se traducen a los códigos públicos de
      [`architecture.md`](architecture.md#mapeo-de-errores); ningún wire code del puente llega al
      agente sin traducir.
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
| 18 tools REST de Fase 0 | Sí | No aplica | Sí: catálogo y casos representativos | Sí |
| Límites de argumentos | Sí: frontera, code points, control y guard de reflexión | No aplica | Sí: rechazo, `maxLength` y contador de peticiones | Pendiente: chat real con el límite |
| Regiones del puente | No | Verificado manualmente | No existe tool | Parcial: crear, leer y borrar |
| Eventos del puente | Sí (`EventBuffer`) | Verificado manualmente | No existe tool | Parcial: región, entrada y comando |
| `BridgeClient` | Sí (falso HTTP) | No aplica | Sí | No |
| `events_since` | Sí (validación) | No aplica | Sí: backlog, cursor, 410, argumentos y degradación | Sí: los 8 tipos de evento capturados por MCP |
| `regions_list`, `region_info` | Sí (validación) | No aplica | Sí: lista, detalle, 404 y argumentos | Sí: lectura contra el mundo real |
| `region_create`, `region_delete` | Sí (política y confirmación) | No aplica | Sí: escritura, duplicado, propietario, confirmación y elicitation | Sí: crear, borrar y política con elicitación real |
| Tools de región | No existen | No aplica | No existen | No |

Esta tabla debe actualizarse en el mismo cambio que la funcionalidad. Si una fila dice «No existe»,
ningún documento puede presentar esa pieza como disponible.

## Qué no es una prueba de aceptación

- Que el proyecto compile.
- Que las pruebas unitarias pasen.
- Que `curl` devuelva `200` contra el puente.
- Que la e2e de Fase 0 pase: cubre las tools REST, no el puente.
- Una prueba manual sin comando reproducible.
