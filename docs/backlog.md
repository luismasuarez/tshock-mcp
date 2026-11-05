# Backlog de mejoras

Mejoras identificadas al comparar el proyecto con el spec MCP 2026-07-28, la guía de seguridad
oficial, el SDK C# 2.3.0 y servidores de referencia como GitHub MCP Server. Cada entrada está
pensada para resolverse en **una sesión de trabajo**.

Este documento es la fuente de verdad del orden de trabajo. El estado por fase sigue en
[`roadmap.md`](roadmap.md); aquí solo se listan mejoras pendientes.

## Cómo usar este documento

1. Elige la primera entrada pendiente cuya columna **Depende de** esté completa.
2. Abre una sesión nueva y pásale solo esa entrada como alcance.
3. Al cerrar, actualiza la fila correspondiente de la tabla de resumen y el registro final.
4. La definición de terminado es la de [`AGENTS.md`](../AGENTS.md): código, pruebas, documentación
   y, si toca una tool, prueba MCP por stdio.

Reglas que ninguna entrada puede romper:

- `stdout` solo transporta JSON-RPC; todo log va a `stderr`.
- El token nunca aparece en logs, errores, auditoría ni salida de tools.
- La escritura sigue cerrada por defecto.
- El sobre `{"ok":...}`/`{"ok":false,...}` no cambia de forma; los cambios son aditivos.
- La configuración es solo por variables de entorno.
- Nada de `rawcmd`, apagado ni mutación de usuarios o prohibiciones.

## Resumen

| # | Mejora | Prioridad | Esfuerzo | Depende de | Estado |
| --- | --- | --- | --- | --- | --- |
| P1-09 | Centralizar los códigos de error | P1 | 1 sesión | — | Pendiente |
| P0-02 | Límites de longitud en argumentos | P0 | 1 sesión | — | Pendiente |
| P0-06 | `title` en las 23 tools | P0 | 1 sesión | — | Pendiente |
| P2-15 | Snapshot del contrato de `tools/list` | P2 | 1 sesión | P0-06 | Pendiente |
| P0-03 | Rate limiting por tool | P0 | 1 sesión | P1-09 | Pendiente |
| P0-04 | Correlación en la auditoría | P0 | 1 sesión | — | Pendiente |
| P0-05 | Paginación opcional en listas | P0 | 1 sesión | — | Pendiente |
| P0-01 | `structuredContent` y `outputSchema` | P0 | 2 sesiones | P1-09, P2-15 | Pendiente |
| P1-07 | Partir las tools por dominio | P1 | 1 sesión | P2-15 | Pendiente |
| P1-08 | Rutas tipadas y capa de dominio | P1 | 2 sesiones | P1-07 | Pendiente |
| P1-11 | Toolsets por variable de entorno | P1 | 1 sesión | P1-07 | Pendiente |
| P1-10 | Resources y prompts | P1 | 2 sesiones | P1-08 | Pendiente |
| P2-12 | Observabilidad con ActivitySource y métricas | P2 | 1 sesión | P1-09 | Pendiente |
| P2-13 | Sonda de arranque del puente | P2 | 1 sesión | — | Pendiente |
| P2-14 | CI más estricto | P2 | 1 sesión | — | Pendiente |
| P2-16 | Pistas de caché en `server/discover` | P2 | 1 sesión | — | Pendiente |
| P2-17 | Versión SemVer real | P2 | 1 sesión | — | Pendiente |
| P3-18 | Distribución como `dotnet tool` | P3 | 1 sesión | P2-17 | Pendiente |
| P3-19 | `server.json` y registro oficial | P3 | 1 sesión | P2-17 | Pendiente |
| P3-20 | Partir `roadmap.md` en estado y futuro | P3 | 1 sesión | — | Pendiente |
| P3-21 | ADRs de las decisiones de arquitectura | P3 | 1 sesión | — | Pendiente |
| P3-22 | Corregir la fase 3 (sampling deprecado) | P3 | 1 sesión | P3-20 | Pendiente |
| P3-23 | Changelog y política de estabilidad | P3 | 1 sesión | P2-17 | Pendiente |

---

## P0 — Contrato y seguridad

### P0-01 · Contenido estructurado y esquema de salida

- **Prioridad:** P0 · **Esfuerzo:** 2 sesiones · **Depende de:** P1-09, P2-15
- **Problema:** las 23 tools devuelven una cadena JSON dentro de `TextContent`. Ninguna declara
  `outputSchema` ni emite `structuredContent`.
- **Evidencia:** `tools/list` inspeccionado con protocolo 2026-07-28: la clave `outputSchema` no
  aparece en ninguna tool. El spec define `structuredContent` y `outputSchema`; GitHub MCP Server
  los emite solo para clientes que negocian 2026-07-28 y mantiene el texto para los anteriores.
- **Cambios:**
  - Investigar primero cómo expone el SDK `UseStructuredContent` y `OutputSchemaType` cuando el
    retorno actual es `string`. Documentar el hallazgo en el commit.
  - Emitir `structuredContent` con el mismo JSON del sobre, conservando el bloque de texto.
  - Declarar `outputSchema` solo en las tools con DTO estable: `server_status`, `players_list`,
    `regions_list`, `bans_list`, `events_since`.
  - No interpretar ni remodelar la carga de TShock: el contenido estructurado es el mismo JSON.
- **Aceptación:**
  - Un cliente 2026-07-28 recibe `structuredContent` y `outputSchema` en las tools elegidas.
  - Un cliente 2025-11-25 recibe exactamente el mismo texto que hoy.
  - El snapshot del contrato se actualiza y la suite sigue verde.
- **Riesgo:** tocar el sobre. Cualquier cambio debe ser aditivo; el texto no cambia.

### P0-02 · Límites de longitud en argumentos

- **Prioridad:** P0 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** `broadcast.message`, `kick_player.reason` y `kill_player.from` solo se validan como
  no vacíos. Un modelo puede enviar megabytes al chat, al log de auditoría o a TShock.
- **Cambios:**
  - Extender el helper de validación a `RequireText(value, name, maxLength)`.
  - Límites propuestos: `broadcast.message` 256, `reason` 200, `from` 200. `region` ya limita a 128.
  - Reutilizar el mismo helper en las tools REST y del puente.
- **Aceptación:** valor por encima del límite devuelve `invalid_argument` antes de la red; pruebas
  unitarias por tool y un check en la e2e.

### P0-03 · Rate limiting por tool

- **Prioridad:** P0 · **Esfuerzo:** 1 sesión · **Depende de:** P1-09
- **Problema:** no hay límite de invocaciones. El spec marca el rate limiting como **MUST**.
- **Cambios:**
  - `TShock/RateLimiter.cs` con token bucket por tool y global, usando `TimeProvider` para poder
    probarlo con reloj falso.
  - Variables nuevas: `TSHOCK_MCP_RATE_LIMIT_PER_MINUTE` (global, por defecto 120) y
    `TSHOCK_MCP_WRITE_RATE_LIMIT_PER_MINUTE` (escrituras, por defecto 20).
  - Código nuevo `rate_limited` con `details.retryAfterMs`. Es aditivo y debe documentarse en
    `docs/tools.md` y en la tabla de errores de `docs/architecture.md`.
- **Aceptación:** pruebas con reloj falso para el límite global y el de escritura; la e2e comprueba
  que el código aparece y que el token no se filtra.

### P0-04 · Correlación en la auditoría

- **Prioridad:** P0 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** el JSONL registra operación, ruta, estado y duración, pero no permite reconstruir
  una sesión: falta el `id` JSON-RPC y la identidad del cliente.
- **Cambios:**
  - Capturar el `RequestContext` en un filtro de invocación de `Program.cs` y publicar
    `requestId`, `clientName` y `clientVersion` en un `AsyncLocal` acotado a la llamada.
  - `AuditLog` añade esos campos. Nunca registra argumentos.
- **Aceptación:** una entrada de auditoría por llamada con `requestId` distinto; el token y los
  argumentos siguen ausentes. Prueba unitaria del filtro y de la entrada generada.

### P0-05 · Paginación opcional en listas

- **Prioridad:** P0 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** `players_list`, `users_list`, `groups_list`, `bans_list` y `regions_list` devuelven
  todo. Con muchos registros se llena el contexto del modelo.
- **Cambios:**
  - Añadir `limit` (por defecto ausente = comportamiento actual) y `cursor` opaco a esas tools.
  - Sin `limit`, la respuesta no cambia. Con `limit`, `data` pasa a ser
    `{"items":[...],"hasMore":true,"nextCursor":"..."}`.
  - Emitir `nextCursor` solo cuando haya más elementos.
- **Aceptación:** pruebas de ambos modos por tool; la e2e verifica que el modo por defecto conserva
  el cuerpo original y que la paginación no pierde ni duplica elementos.
- **Riesgo:** cambiar el significado de `data` cuando se pagina. Documentarlo como modo opt-in.

### P0-06 · `title` en las 23 tools

- **Prioridad:** P0 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** ninguna tool declara `title`. El spec lo soporta y las interfaces lo muestran.
- **Cambios:** añadir `Title` a cada `[McpServerTool]`, en inglés y en formato legible
  («Server status», «List players»). Actualizar `docs/tools.md`.
- **Aceptación:** prueba que exige `title` no vacío en las 23; snapshot actualizado.

---

## P1 — Arquitectura

### P1-07 · Partir las tools por dominio

- **Prioridad:** P1 · **Esfuerzo:** 1 sesión · **Depende de:** P2-15
- **Problema:** `BridgeTools.cs` tiene 406 líneas y `ReadTools.cs` 156. Mezcla eventos, regiones y
  escritura en un mismo archivo.
- **Cambios:** un archivo por dominio dentro de `Tools/`: `PlayersTools`, `AccountsTools`,
  `WorldTools`, `BansTools`, `RegionsTools`, `EventsTools`, con `[McpServerToolType]` por clase.
  Los helpers compartidos van a `Tools/ToolSupport.cs`.
- **Aceptación:** mismas 23 tools, mismos nombres y descripciones; snapshot actualizado solo si
  cambia el orden. Verificar que el orden sigue siendo determinista entre procesos.
- **Nota:** al repartir clases puede cambiar el orden de `tools/list`. Es aceptable si sigue siendo
  estable; documentar el cambio en el commit.

### P1-08 · Rutas tipadas y capa de dominio

- **Prioridad:** P1 · **Esfuerzo:** 2 sesiones · **Depende de:** P1-07
- **Problema:** las tools construyen rutas y diccionarios de consulta a mano. Cambiar una ruta exige
  buscar cadenas en tres archivos.
- **Cambios:**
  - `TShock/Routes.cs` con las rutas como constantes.
  - Una capa fina por dominio (`IPlayersApi`, `IWorldApi`, `IAccountsApi`, `IBansApi`) que envuelva
    `ITShockClient` y exponga métodos con nombre.
  - Las tools dejan de conocer rutas y nombres de parámetros de TShock.
- **Aceptación:** `grep` de rutas en `Tools/` no devuelve literales; las pruebas existentes siguen
  pasando sin cambios de contrato.

### P1-09 · Centralizar los códigos de error

- **Prioridad:** P1 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** 20 códigos repetidos como literales en todo `src/`. Un typo produce un código que
  ningún cliente conoce.
- **Cambios:** `TShock/ErrorCodes.cs` con constantes públicas; reemplazar todos los literales.
  Añadir una prueba que compare el conjunto con la tabla de `docs/architecture.md`.
- **Aceptación:** no quedan literales fuera de `ErrorCodes.cs`; la prueba falla si se agrega un
  código sin documentarlo.

### P1-10 · Resources y prompts

- **Prioridad:** P1 · **Esfuerzo:** 2 sesiones · **Depende de:** P1-08
- **Problema:** solo se expone la primitiva `tools`. El spec define tres.
- **Cambios:**
  - Resources de solo lectura: `tshock://server/status`, `tshock://world`, `tshock://players`,
    `tshock://bans`, `tshock://regions`.
  - Plantillas: `tshock://players/{name}`, `tshock://regions/{name}`.
  - Prompts: `diagnose-server`, `moderate-player`, `audit-permissions`, con argumentos tipados y
    referencias a las tools existentes.
  - La redacción de IP y credenciales aplica igual que en las tools.
- **Aceptación:** `resources/list`, `resources/read`, `resources/templates/list`, `prompts/list` y
  `prompts/get` verificados por MCP en la e2e.

### P1-11 · Toolsets por variable de entorno

- **Prioridad:** P1 · **Esfuerzo:** 1 sesión · **Depende de:** P1-07
- **Problema:** el catálogo completo son 23 tools; un operador que solo quiere consultar estado paga
  ese contexto en cada conversación.
- **Cambios:**
  - `TSHOCK_MCP_TOOLSETS` con grupos `core`, `players`, `accounts`, `world`, `bans`, `regions`,
    `events` y el valor especial `all`.
  - Por defecto `all`: el catálogo no cambia para nadie que no configure la variable.
  - Validación estricta al arrancar: un grupo desconocido impide el arranque.
- **Aceptación:** con el valor por defecto se listan las 23; con `core,players` se listan solo las
  del grupo; la e2e cubre ambos casos.
- **Riesgo:** el invariante 5 exige catálogo estable. Al ser opt-in y con `all` por defecto, se
  cumple; documentarlo así.

---

## P2 — Operación y calidad

### P2-12 · Observabilidad con ActivitySource y métricas

- **Prioridad:** P2 · **Esfuerzo:** 1 sesión · **Depende de:** P1-09
- **Problema:** no hay trazas ni métricas; solo logs a `stderr`. El spec deprecó el primitivo
  `logging` y recomienda `stderr` u OpenTelemetry.
- **Cambios:**
  - `Telemetry/Telemetry.cs` con `ActivitySource` y `Meter`.
  - Instrumentos: `tshock.tool.calls`, `tshock.tool.duration`, `tshock.upstream.duration`,
    `tshock.errors` etiquetado por código (nunca por argumento).
  - Exportación OTLP opcional por `OTEL_EXPORTER_OTLP_ENDPOINT`; sin la variable no se exporta nada.
- **Aceptación:** una prueba con `MeterListener`/`ActivityListener` comprueba que se emiten
  instrumentos y que ninguna etiqueta contiene el token ni la IP.

### P2-13 · Sonda de arranque del puente

- **Prioridad:** P2 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** un puente mal configurado solo se descubre al invocar una tool.
- **Cambios:** si `TSHOCK_BRIDGE_*` está configurado, `GET /v1/health` al arrancar con timeout de 2 s
  y log a `stderr` con versión y estado. Nunca falla el arranque ni imprime el token.
- **Aceptación:** prueba con manejador HTTP falso para los casos sano, caído y timeout.

### P2-14 · CI más estricto

- **Prioridad:** P2 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** el workflow solo compila y ejecuta pruebas.
- **Cambios:** añadir `dotnet format --verify-no-changes`, `dotnet list package --vulnerable
  --include-transitive`, cobertura con umbral, CodeQL y matriz .NET 8/9.
- **Aceptación:** el workflow pasa en verde y falla ante un paquete vulnerable o formato incorrecto.

### P2-15 · Snapshot del contrato de `tools/list`

- **Prioridad:** P2 · **Esfuerzo:** 1 sesión · **Depende de:** P0-06
- **Problema:** el invariante 5 depende de revisión manual; un cambio accidental de nombre,
  descripción o hint pasaría desapercibido.
- **Cambios:** volcar `tools/list` a `tests/contract/tools-list.snapshot.json` y comparar en la e2e.
  El snapshot incluye nombres, títulos, descripciones, `inputSchema`, `annotations` y orden.
- **Aceptación:** cualquier diferencia hace fallar la prueba y muestra el diff.

### P2-16 · Pistas de caché en `server/discover`

- **Prioridad:** P2 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** `server/discover` responde `ttlMs: 0` y `cacheScope: private`.
- **Cambios:** declarar 5 minutos y `public`, ya que el catálogo es estático. Investigar la API del
  SDK para configurarlo.
- **Aceptación:** `server/discover` devuelve los valores nuevos y la e2e lo verifica.

### P2-17 · Versión SemVer real

- **Prioridad:** P2 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** `serverInfo.version` es `0.1.0.0`, la versión de ensamblado.
- **Cambios:** fijar `<Version>` o adoptar MinVer a partir de tags. El tag `v0.1.0` ya existe.
- **Aceptación:** `server/discover` y `initialize` muestran `0.1.0`; la release y el paquete usan la
  misma versión.

---

## P3 — Distribución y documentación

### P3-18 · Distribución como `dotnet tool`

- **Prioridad:** P3 · **Esfuerzo:** 1 sesión · **Depende de:** P2-17
- **Cambios:** `PackAsTool`, `ToolCommandName=tshock-mcp`, `PackageId` y workflow de publicación.
  Documentar `dotnet tool install -g tshock-mcp`.
- **Aceptación:** instalación global desde el paquete local y arranque por stdio con el handshake.

### P3-19 · `server.json` y registro oficial

- **Prioridad:** P3 · **Esfuerzo:** 1 sesión · **Depende de:** P2-17
- **Problema:** terceros listan el proyecto sin permiso. Publicar en el registro oficial devuelve el
  control sobre la ficha y la versión.
- **Cambios:** `server.json` conforme al esquema del registro, workflow de publicación y sección en
  el README.
- **Aceptación:** el validador del registro acepta el archivo y la ficha apunta al repositorio
  correcto.

### P3-20 · Partir `roadmap.md` en estado y futuro

- **Prioridad:** P3 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** el documento se contradice: dice 18 tools, «hoy no hay ninguna tool del puente»,
  213 pruebas y «repositorio local sin remoto».
- **Cambios:** `docs/status.md` con el estado actual (generado o verificado por CI) y `roadmap.md`
  solo con lo que falta. Enlazar este backlog.
- **Aceptación:** ningún número contradice la realidad; la e2e o un script comprueba el conteo de
  tools y pruebas.

### P3-21 · ADRs de las decisiones de arquitectura

- **Prioridad:** P3 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Cambios:** `docs/adr/` con plantilla y una entrada por decisión ya tomada: gateway externo,
  licencia, stdio primero, sobre estable, configuración por entorno, redacción, .NET 8/9.
- **Aceptación:** cada ADR tiene contexto, decisión, consecuencias y alternativas descartadas.

### P3-22 · Corregir la fase 3 (sampling deprecado)

- **Prioridad:** P3 · **Esfuerzo:** 1 sesión · **Depende de:** P3-20
- **Problema:** el roadmap propone *sampling* de MCP para el asistente del chat, pero el spec lo
  marca deprecado desde 2026-07-28.
- **Cambios:** reescribir esa fase para usar una API de proveedor configurada por el operador y
  documentar la deprecación.
- **Aceptación:** no queda ninguna mención a sampling como plan vigente.

### P3-23 · Changelog y política de estabilidad

- **Prioridad:** P3 · **Esfuerzo:** 1 sesión · **Depende de:** P2-17
- **Cambios:** `CHANGELOG.md` y `docs/stability.md` con la regla: nombres de tool, sobre y códigos de
  error son contrato; cambiarlos exige versión mayor.
- **Aceptación:** el README enlaza ambos y la primera entrada cubre lo ya publicado.

---

## Orden sugerido

Las dependencias mandan, pero este orden maximiza el valor temprano:

1. **P1-09** — barato y desbloquea el resto.
2. **P0-02** — cierra un agujero real.
3. **P0-06** — contrato visible, casi gratis.
4. **P2-15** — congela el contrato antes de tocarlo.
5. **P0-03**, **P0-04**, **P0-05**.
6. **P0-01** — la más grande de P0.
7. **P1-07**, **P1-08**, **P1-11**, **P1-10**.
8. **P2-12**, **P2-13**, **P2-14**, **P2-16**, **P2-17**.
9. **P3-18** a **P3-23**.

## Plantilla de prompt para cada sesión

```text
Implementa la entrada <ID> de docs/backlog.md.

Antes de tocar código:
- Lee AGENTS.md, docs/backlog.md (solo esa entrada) y los archivos que menciona.
- Confirma el alcance y las dependencias.

Al terminar:
- Ejecuta dotnet build -c Release, dotnet test -c Release y
  python3 tests/e2e/mcp_stdio_smoke.py.
- Actualiza docs en el mismo cambio y la fila de estado de docs/backlog.md.
- Un solo commit con mensaje en inglés.
```

## Registro

| Fecha | Entrada | Commit | Notas |
| --- | --- | --- | --- |
| 2026-11-05 | Creación del backlog | — | Revisión contra spec 2026-07-28, security best practices, SDK 2.3.0 y GitHub MCP Server. |
