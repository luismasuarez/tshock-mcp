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
| P1-09 | Taxonomía única de códigos de error | P1 | 1 sesión | — | Hecho |
| P0-02 | Límites y política de argumentos de texto | P0 | 1 sesión | — | Hecho |
| P0-06 | Títulos de tool como contrato | P0 | 1 sesión | — | Hecho |
| P2-15 | Snapshot del contrato de `tools/list` | P2 | 1 sesión | P0-06 | Hecho |
| P0-03 | Rate limiting por tool | P0 | 1 sesión | P1-09 | Hecho |
| P0-04 | Correlación en la auditoría | P0 | 1 sesión | — | Pendiente |
| P0-05 | Paginación opcional en listas | P0 | 1 sesión | — | Pendiente |
| P0-01 | `structuredContent` y `outputSchema` | P0 | 2 sesiones | P1-09, P2-15 | Pendiente |
| P0-07 | `isError` coherente con el sobre | P0 | 1 sesión | P0-01 | Pendiente |
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

### P0-02 · Límites y política de argumentos de texto

- **Prioridad:** P0 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** el agujero no son tres campos, es una clase: todo `string` que entra por una tool y
  termina en el chat, en la URL de TShock, en el log/auditoría o en el contexto del modelo. Hoy
  `RequireText` solo rechazaba vacío; `player`, `user`, `group`, `message`, `reason` y `from` no
  tenían cota; `region`/`owner` tenían un 128 local y sin política común. Un modelo podía enviar
  megabytes, CRLF/ANSI, bidi o zero-width: inundar el chat, inflar el contexto, forjar líneas de log
  o hacer que TShock devolviera el texto en un `rest_error`. El SDK 2.3.0 documenta que las
  anotaciones de datos decoran el `inputSchema` pero **no** se validan en runtime; `tools/list` no
  publicaba `maxLength`.
- **Evidencia:** `tools/list` del binario anterior: `broadcast.message`, `kick_player.reason` y
  `kill_player.from` eran `{"type":"string"}` sin `maxLength`. `WriteTools.RequireText` y
  `ReadTools.RequireText` solo comprobaban `IsNullOrWhiteSpace`; `BridgeTools.RequireRegionName`
  repetía la comprobación con 128 local.
- **Cambios:**
  - Un solo validador en `Tools/ToolSupport.cs`: `RequireText(value, name, maxLength)` con conteo de
    **code points Unicode** (semántica de `maxLength` en JSON Schema), no `string.Length` (unidades
    UTF-16), y rechazo de caracteres de control/formato (C0/C1, DEL, bidi, zero-width, U+2028/2029;
    se conservan ZWNJ y ZWJ). `RequireRegionName` delega y añade la regla de `/`.
  - Tabla única `ArgumentLimits` con constantes `const int`: `broadcast.message` 256,
    `kick_player.reason` 200, `kill_player.from` 64 (nombre de jugador, alineado con
    `EventText.PlayerNameMax` del puente), `player` 64, `user`/`group` 64, `type` 16,
    `region.name`/`owner` 128. Sin variables de entorno: los límites son contrato, no configuración.
  - `[MaxLength(ArgumentLimits.X)]` en cada parámetro para que `inputSchema` publique `maxLength`;
    el runtime sigue siendo la autoridad porque el SDK no aplica la anotación.
  - Se mantiene la precedencia actual: `write_disabled` antes de validar; el sobre y
    `invalid_argument` no cambian; el mensaje de error no repite el valor recibido.
  - Test de inventario por reflexión: todo parámetro `string` de una `[McpServerTool]` debe tener
    `[MaxLength]` y un límite en la tabla; una tool nueva sin límite rompe la suite.
  - Documentado en `docs/tools.md`, `docs/architecture.md`, `docs/security.md`, `docs/testing.md` y
    `README.md`.
- **Aceptación:** para cada campo, `max-1`, `max`, `max+1`, vacío, CRLF, NUL, ANSI, bidi y zero-width
  devuelven `invalid_argument` antes de cualquier petición; `max` llega intacto al upstream. El
  conteo es por code points (`max` emoji se acepta y `max+1` se rechaza). `tools/list` publica
  `maxLength`. La e2e comprueba el rechazo, que el contador de peticiones del TShock/puente falso no
  crece, que el payload no aparece en `stderr` y que el token sigue ausente. El test de reflexión
  falla si se agrega un `string` sin límite.
- **Riesgo:** endurecer el contrato es observable; los límites son holgados y se documentan. La
  validación de la tool ocurre después de parsear el JSON-RPC: un tope real de megabytes pertenece
  al transporte y queda como riesgo residual documentado, no como parte de esta entrada.

### P0-03 · Rate limiting por tool

- **Prioridad:** P0 · **Esfuerzo:** 1 sesión · **Depende de:** P1-09 (hecho)
- **Problema:** el spec 2026-07-28 obliga a *«Rate limit tool invocations»* y hoy no hay límite. Un
  modelo en bucle, un reintento del host o una secuencia de escrituras llega íntegra a TShock. En
  stdio hay un cliente por proceso: el objetivo no es una cuota multiinquilino, sino acotar el daño
  local y mantener el servidor de juego disponible.
- **Evidencia:** el texto normativo no fija algoritmo; el SDK 2.3.0 empareja el primitivo antes de
  los filtros, así que `MatchedPrimitive` está disponible; una tool inexistente ya llega al filtro
  (hoy devuelve `unexpected_error`) y no debe crear estado por nombre.
- **Diseño:**
  - `TShock/RateLimiter.cs`: token bucket con `TimeProvider` monotónico. Tres cubos:
    global (`TSHOCK_MCP_RATE_LIMIT_PER_MINUTE`, default 120), escritura
    (`TSHOCK_MCP_WRITE_RATE_LIMIT_PER_MINUTE`, default 20) y tool.
  - El cubo por tool usa `max(1, classRate / 2)`: fair share de código, sin tercera variable. Una
    lectura puede consumir hasta 60/min y una escritura hasta 10/min con los defaults.
  - Capacidad = tasa por minuto; recarga continua = tasa/60 por segundo. El burst máximo es un
    minuto de presupuesto.
  - **Adquisición atómica:** se recargan y comprueban los cubos aplicables y se consumen todos o
    ninguno. Si el de escritura deniega, el global no se descuenta. `retryAfterMs` es el máximo de
    los cubos denegados, con techo y mínimo 1.
  - **Clasificación:** escritura si `MatchedPrimitive is McpServerTool` y
    `ProtocolTool.Annotations.ReadOnlyHint != true`; un primitivo ausente solo consume el global. Sin
    lista manual y sin indexar por `params.name`.
  - **Punto de aplicación:** filtro `AddCallToolFilter` de `Program.cs`, antes del binding. Cuenta
    toda invocación, incluidos argumentos inválidos, `write_disabled` y reintentos MRTR.
  - **Reloj:** `GetTimestamp`/`GetElapsedTime`; un salto hacia atrás no crea fichas.
  - **Configuración:** enteros `1..60000`, sin `0`; ausente usa el default, inválido impide el
    arranque. La variable de escritura se valida aunque `TSHOCK_MCP_ALLOW_WRITE=false`.
  - **Error:** `rate_limited`, `details.retryAfterMs` entero, sin `httpStatus`, `isError: true`
    porque se rechaza antes de ejecutar. Se añade a `ErrorCodes.All` y a la tabla canónica.
  - **Registro:** `LogDebug` con tool, ámbito y espera; nunca argumentos ni token.
- **Cambios:** `TShock/RateLimiter.cs`, `Tools/ToolRateLimitCatalog.cs`,
  `Configuration/TShockOptions.cs`, `Program.cs`, `TShock/ErrorCodes.cs`; pruebas unitarias
  (`RateLimiterTests`, `ToolRateLimitCatalogTests`, `TShockOptionsTests`, `ErrorCodesTests`); e2e con
  dos procesos de presupuesto bajo; `docs/architecture.md`, `docs/tools.md`, `docs/security.md`,
  `docs/testing.md`, `README.md`, `docs/roadmap.md` y esta entrada.
- **Aceptación:**
  - Unidad con reloj falso: global, escritura, fair share, recarga exacta, reloj hacia atrás,
    atomicidad (una escritura denegada no cobra el global), 100 llamadas concurrentes con cupo 10 →
    exactamente 10, tool inexistente y configuración inválida.
  - Reflexión: las 23 tools declaran `readOnlyHint` y la clasificación coincide con esa anotación.
  - E2E: primera llamada `ok` y segunda `rate_limited` con `isError: true` y
    `details.retryAfterMs` en `(0, 60000]`; el contador del TShock falso crece exactamente una vez;
    lo mismo para `broadcast` con el cupo de escritura; el token no aparece en `stderr`.
  - El snapshot de `tools/list` sigue idéntico. Build, pruebas y e2e verdes.
- **Riesgo:** el límite es por proceso y en memoria, se reinicia con el proceso y varios procesos
  multiplican el cupo. No acota llamadas en vuelo: el *bulkhead* de concurrencia queda como entrada
  aparte y se documenta como riesgo residual. Un valor alto es una decisión deliberada del operador.
- **Fuera de alcance:** semáforo de concurrencia, cuotas por cliente de Fase 2, métricas (P2-12),
  correlación de auditoría (P0-04) y límites distribuidos.

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

### P0-06 · Títulos de tool como contrato de interfaz

- **Prioridad:** P0 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** las 23 tools no declaran `title`, así que la interfaz cae al `name` programático
  (`server_status`, `kick_player`). El spec define `title` en `BaseMetadata` y en `ToolAnnotations`,
  con precedencia `title` > `annotations.title` > `name`, y lo describe como nombre para UI, no
  como descripción para el modelo. Sin política, los títulos se duplican, mezclan gramáticas, usan
  jerga o hacen que una acción destructiva parezca inofensiva en el diálogo de consentimiento.
  Tampoco hay guard: una tool 24 puede entrar sin título.
- **Evidencia:** captura de `tools/list` del binario anterior bajo 2025-06-18, 2025-11-25 y
  2026-07-28 (`server/discover` + `_meta`): 23 tools, `title` ausente en 23/23 y
  `annotations.title` ausente en 23/23. En el SDK 2.3.0, `McpServerToolAttribute.Title` alimenta
  `Tool.Title` y `Annotations.Title` (`AIFunctionMcpServerTool`), y la ruta legacy
  (`BuildLegacyWireProtocolTool`) copia ambos; el campo es aditivo en las tres revisiones.
- **Política:** inglés, imperativo + objeto, sentence case, 40 caracteres como máximo, solo
  letras/dígitos/espacios, sin punto final, sin acrónimos ni jerga, único en el catálogo, distinto
  del `name`, estático (nunca derivado de datos del servidor) y sin prefijo «TShock». Las tools
  destructivas usan verbos explícitos. `tools/list` no negocia locale: se elige un idioma coherente
  para todo el catálogo, no una mezcla.
- **Cambios:**
  - `Tools/ToolTitles.cs` con una constante por tool y la política en el comentario.
  - `Title = ToolTitles.X` en las 23 `[McpServerTool]`; no se tocan nombres, descripciones, hints,
    rutas ni el sobre.
  - `ToolTitleTests`: registro real por ensamblado; tabla esperada independiente de 23 títulos;
    no vacíos, únicos, distintos del `name`, ≤ 40, sentence case, sin espacios dobles ni punto
    final, `Title == Annotations.Title`; una tool nueva sin título o fuera de la tabla falla.
  - e2e: un cliente legacy (2025-06-18) y uno moderno (`server/discover` + `_meta` 2026-07-28)
    reciben los 23 títulos; el resto del payload no cambia.
  - `docs/tools.md` con la tabla canónica de títulos y la precedencia; `docs/testing.md` con la
    prueba; una frase en `docs/security.md` sobre títulos explícitos en operaciones destructivas.
- **Aceptación:** 23/23 títulos no vacíos en `tools/list` en las dos eras; `title` y
  `annotations.title` coinciden; unicidad e invariantes verdes; el diff del payload solo agrega
  esos campos; una tool nueva sin título rompe la suite; build, pruebas y e2e verdes.
- **Riesgo:** `title` es aditivo y no cambia la invocación ni el sobre. Un cliente puede refrescar
  su caché de `tools/list`; es esperable. Un título engañoso es un riesgo de consentimiento, por
  eso las destructivas usan verbos explícitos. El snapshot completo de `tools/list` sigue siendo
  P2-15: aquí solo se fija la tabla de títulos y sus invariantes.

### P0-07 · `isError` coherente con el sobre

- **Prioridad:** P0 · **Esfuerzo:** 1 sesión · **Depende de:** P0-01
- **Problema:** los errores de negocio devuelven `{"ok":false,...}` con `isError:false`; solo las
  excepciones de binding del filtro de `Program.cs` marcan `IsError = true`. Un cliente que
  ramifica por el flag del protocolo trata un fallo como éxito.
- **Evidencia:** la e2e comprueba `isError:true` solo para argumentos faltantes o mal tipados; una
  llamada con `write_disabled` o `rest_error` devuelve el sobre con `isError` ausente o falso.
- **Cambios:** investigar cómo el SDK 2.3.0 permite devolver `CallToolResult` desde una tool, o
  inspeccionar el texto en un filtro de respuesta; marcar `IsError=true` cuando el sobre tenga
  `ok:false`, sin cambiar el texto ni el contenido estructurado.
- **Aceptación:** todo `ok:false` sale con `isError:true` y todo `ok:true` con `isError:false`; la
  e2e lo cubre para `invalid_argument`, `write_disabled`, `rest_error` y `region_not_found`; los
  clientes anteriores siguen recibiendo el mismo texto.
- **Riesgo:** un filtro que reparsea el sobre puede alterar el contrato o duplicar trabajo; el texto
  debe quedar byte a byte igual.

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

### P1-09 · Taxonomía única y verificable de códigos de error

- **Prioridad:** P1 · **Esfuerzo:** 1 sesión · **Depende de:** —
- **Problema:** no eran 20 literales sueltos. Hay 20 códigos públicos del sobre, 17 códigos internos
  del protocolo del puente, tres constantes locales duplicadas (`InvalidArgumentCode`), un
  `unknown_error` sin documentar y cinco documentos con listas que ya divergían. Un typo o un wire
  code nuevo podía llegar al agente sin que nada lo detectara.
- **Cambios:**
  - `TShock/ErrorCodes.cs` con los 20 códigos públicos como constantes, `All` e `IsKnown`.
  - `TShock/BridgeWireCodes.cs` con los wire codes que el gateway traduce y
    `TShock.AgentBridge/WireCodes.cs` con los que el plugin emite. Son capas distintas y no se
    comparten por TFM (`net8.0` frente a `net9.0`); la traducción se prueba en las tools.
  - Reemplazo de todos los literales en los puntos de construcción; se eliminaron las tres copias
    de `InvalidArgumentCode`.
  - `docs/architecture.md` como tabla canónica del sobre, con marcadores; `docs/bridge.md` con la
    tabla de wire codes; `docs/tools.md`, `docs/testing.md` y `README.md` enlazan a la canónica en
    lugar de repetir listas.
  - Pruebas: conjunto exacto, ausencia de duplicados, igualdad bidireccional con la documentación,
    rechazo de literales en los constructores y una comprobación en la e2e de que todo `error.code`
    observado pertenece a la tabla canónica.
- **Aceptación:** no quedan literales de códigos en los puntos de construcción; agregar un código
  sin documentarlo falla, y documentar uno inexistente también; los sobres actuales no cambian ni
  en forma ni en texto; build, pruebas y e2e quedan verdes.
- **Riesgo:** los códigos son API pública; este trabajo no renombra ninguno. `unknown_error` se
  documenta como fallback interno. La prueba de fuente se limita a `src/` y a los constructores,
  porque las descripciones de las tools mencionan códigos como texto de ayuda.

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
- **Problema:** el invariante 5 depende de revisión manual; un cambio accidental de nombre, título,
  descripción, `inputSchema`, anotación u orden pasaría desapercibido. Además, nada demostraba que
  el catálogo fuera idéntico entre las eras de protocolo ni entre estados de configuración que no
  deberían afectarlo.
- **Evidencia:** captura del binario Release por stdio. En `2025-06-18` el resultado solo tiene
  `tools`; en `2026-07-28` añade `resultType`, `ttlMs`, `cacheScope` y `_meta`. Los 23 objetos de
  tool son idénticos en ambas eras (`name`, `title`, `description`, `inputSchema`, `annotations`).
  Seis procesos distintos produjeron el mismo hash canónico; `TSHOCK_MCP_ALLOW_WRITE=false` no
  cambió el catálogo. El contrato es el catálogo, no el sobre.
- **Cambios:**
  - `tests/contract/tools_list_contract.py`: captura por stdio para `2025-06-18`, `2025-11-25` y
    `2026-07-28`; sigue `nextCursor`; normaliza a `{formatVersion, order, tools}` con claves
    ordenadas; valida invariantes estructurales; compara por nombre y orden; reporta rutas de campo
    y diff; modos `--check`, `--write` (rechazado con `CI=true`) y `--self-test`.
  - `tests/contract/tools-list.snapshot.json`: artefacto generado con las 23 tools, sin campos de
    sobre, versión ni datos de entorno.
  - `tests/e2e/mcp_stdio_smoke.py`: ejecuta el self-test; compara el snapshot en cliente legacy,
    `2025-11-25` y moderno; exige paridad entre eras y entre escritura on/off y puente
    presente/detenido; imprime el diff ante cualquier diferencia.
  - `tests/contract/README.md`: procedencia, comando de regeneración, política de cambios (aditivo
    frente a ruptura), por qué se excluye el sobre y cómo revisar un diff.
  - `docs/testing.md` con la prueba y el procedimiento; `docs/tools.md` con el puntero al snapshot;
    `README.md` y `docs/testing.md` con los conteos reales; esta fila y el registro.
- **Aceptación:**
  - `--check` pasa en un build limpio y `--write` reproduce el archivo byte a byte.
  - Cualquier cambio de nombre, título, descripción, `inputSchema`, anotación u orden hace fallar
    la e2e con la ruta del campo y el diff.
  - El self-test demuestra que cada clase de mutación se detecta.
  - El catálogo es idéntico en las tres eras y en los estados de configuración probados.
  - Dos procesos independientes producen el mismo catálogo normalizado.
  - Build, pruebas y e2e verdes; comprobación manual opcional con el Inspector oficial.
- **Riesgo:** un snapshot demasiado rígido frena cambios legítimos. Se mitiga con catálogo (no
  sobre), formato canónico, comando de actualización explícito y diff por campo. P1-07 puede
  cambiar el orden: se actualiza el snapshot en ese commit y el diff lo evidencia. No se implementa
  `structuredContent` (P0-01) ni se congela `server/discover` (P2-16/P2-17).

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

1. **P0-02** — cierra un agujero real.
2. **P0-06** — contrato visible, casi gratis.
3. **P2-15** — congela el contrato antes de tocarlo.
4. **P0-04**, **P0-05** (P0-03 ya está hecho).
5. **P0-01** — la más grande de P0.
6. **P0-07** — alinea el flag del protocolo con el sobre.
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
| 2026-11-05 | P1-09 · Taxonomía única de códigos de error | — | Commit único; 20 códigos públicos y 17 wire codes centralizados, tablas canónicas y pruebas bidireccionales. |
| 2026-11-05 | P0-02 · Límites y política de argumentos de texto | — | Commit único; code points, control/formato, `maxLength` en `inputSchema`, guard de reflexión y e2e con contador de peticiones. |
| 2026-11-05 | P0-06 · Títulos de tool como contrato de interfaz | — | Commit único; 23 títulos con política, guard de catálogo y verificación legacy/modern. |
| 2026-11-05 | P2-15 · Snapshot del contrato de `tools/list` | — | Commit único; snapshot generado, paridad de tres eras, invariancia de configuración y self-test del comparador. |
| 2026-11-06 | P0-03 · Rate limiting por tool | — | Commit único; cubos global/escritura/tool con adquisición atómica, reloj falso, `rate_limited` con `retryAfterMs` y e2e con contador de peticiones. |
