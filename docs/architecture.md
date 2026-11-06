# Arquitectura

## Visión general

`tshock-mcp` es un proceso local de .NET 8 que actúa como **puerta de enlace** entre un anfitrión
MCP y la API REST de TShock. No se instala dentro del servidor de Terraria, no modifica TShock y no
necesita plugins adicionales para la Fase 0: se comunica con la API REST que TShock ya expone. La
Fase 1 agrega un plugin opcional que se describe en [bridge.md](bridge.md).

```
+------------------------------+        MCP (JSON-RPC sobre stdio)        +------------------------------+
| Anfitrión de IA              |  ----------------------------------->   | tshock-mcp                   |
| Claude Desktop / VS Code /   |                                         |  - Servidor MCP (stdio)      |
| Cursor / cliente MCP         |  <-----------------------------------   |  - 23 herramientas           |
+------------------------------+        stdout: JSON-RPC, stderr: logs   |  - Cliente HTTP de TShock    |
                                                                         |  - Sobre de resultados       |
                                                                         +--------------+---------------+
                                                                                        | HTTP/HTTPS + token
                                                                                        | (query param)
                                                                         +--------------v---------------+
                                                                         | Servidor TShock              |
                                                                         |  - Terraria + TShock         |
                                                                         |  - REST API habilitada       |
                                                                         +------------------------------+
```

## Capas y estado actual

La Fase 0 está completa. La Fase 1 tiene el plugin y su primer consumidor MCP:

```
+------------------+   MCP/stdio   +----------------------+   REST 7878   +-------------+
| Cliente MCP      |<------------->| tshock-mcp           |<------------->| TShock      |
+------------------+               | 18 tools REST        |               +-------------+
                                   | 5 tools del puente   |
                                   | + BridgeClient       |   HTTP 7879   +-------------+
                                   +----------------------+<------------->| AgentBridge |
                                                                          | regiones +  |
                                                                          | eventos     |
                                                                          +-------------+
```

Cinco tools consumen el puente: `events_since`, `regions_list`, `region_info`, `region_create` y
`region_delete`, todas por `tool MCP -> BridgeClient -> HTTP /v1/* -> plugin`. Las rutas de comandos
siguen siendo capa inferior hasta que exista su tool, y la degradación está probada: con el puente
caído, las 18 tools REST siguen funcionando. Ver [`testing.md`](testing.md).

Componentes internos:

| Componente | Responsabilidad |
| --- | --- |
| `Program.cs` | Construye el host, configura logging a stderr, registra el transporte stdio y descubre las herramientas por ensamblado. |
| `Configuration/TShockOptions.cs` | Lee y valida las variables de entorno; falla al arrancar si la configuración es inválida. |
| `TShock/ITShockClient` | Ejecuta peticiones GET contra la API REST, aplica timeout y traduce fallos a resultados tipados. |
| `TShock/ToolResults` | Serializa el sobre estable `ok` / `error`. |
| `TShock/ErrorCodes.cs` | Catálogo público y cerrado de códigos del sobre; la tabla canónica se valida contra él. |
| `TShock/RateLimiter.cs` | Presupuesto de invocaciones con cubos global, de escritura y por tool, reloj monotónico y adquisición atómica. |
| `TShock/BridgeWireCodes.cs` | Discriminadores internos del puente que el gateway traduce a códigos públicos. |
| `Tools/ReadTools.cs` | 12 herramientas sin efectos secundarios. |
| `Tools/WriteTools.cs` | 6 herramientas de mutación, bloqueadas salvo que se habilite la escritura. |
| `TShock/IBridgeClient` | Cliente del puente: bearer por petición, tope de respuesta y códigos de error estables. |
| `Tools/BridgeTools.cs` | Herramientas del puente: eventos, lectura y escritura de regiones, con confirmación y política de propietario. Siempre registradas; sin puente responden `bridge_unavailable`. |

## Por qué una puerta de enlace externa

Se evaluaron tres alternativas antes de fijar la Fase 0:

1. **Bifurcar (*fork*) TShock.** Obliga a mantener un servidor modificado, a seguir cada versión de
   Terraria y de TShock y a que cada operador instale un binario no oficial. El costo de
   mantenimiento y el riesgo de seguridad recaen sobre el proyecto.
2. **Plugin en proceso dentro de TShock.** Requiere cargar código de terceros en el servidor,
   comparte su dominio de fallos y ata el ciclo de vida del agente al del juego. Un fallo del
   plugin puede tumbar el servidor; una actualización de TShock puede romper el plugin.
3. **Puerta de enlace externa sobre la API REST.** Es la opción elegida: TShock ya expone las rutas
   necesarias con su propio modelo de permisos, el servidor no se modifica y el agente corre en un
   proceso separado con su propio ciclo de vida, límites y configuración.

Ventajas concretas:

- **Aislamiento de fallos.** Si el servidor MCP se cae, Terraria sigue funcionando.
- **Actualizaciones independientes.** El agente se actualiza sin tocar el servidor de juego.
- **Reutilización del modelo de permisos.** TShock sigue siendo la autoridad: el token y su grupo
  determinan qué rutas se permiten, además del filtro propio de escritura.
- **Despliegue simple.** Un binario y variables de entorno; también una imagen de contenedor.
- **Auditoría centralizada.** Todas las llamadas pasan por un único punto donde se puede registrar
  qué se pidió, con qué resultado y cuánto tardó.

## Flujo de una petición

1. El anfitrión MCP arranca `tshock-mcp` como proceso hijo y completa el handshake JSON-RPC por
   stdin/stdout.
2. El modelo invoca una herramienta, por ejemplo `player_info` con `player = "Alice"`.
3. El SDK de MCP empareja el primitivo, deserializa los argumentos y prepara la llamada.
4. El filtro de `Program.cs` comprueba el presupuesto de invocaciones antes del binding. Si el cubo
   global, el de escritura o el de la tool no tiene un token, devuelve `rate_limited` con
   `details.retryAfterMs` sin ejecutar la tool ni tocar la red.
5. La herramienta valida el argumento antes de la red: falta, vacío, tipo incorrecto, más largo que
   su límite en code points o con caracteres de control produce `invalid_argument`. El límite se
   publica además como `maxLength` en el `inputSchema`; la validación en runtime es la autoridad.
6. Si es una herramienta de escritura y `TSHOCK_MCP_ALLOW_WRITE` es `false`, devuelve
   `write_disabled` sin tocar la red.
7. `ITShockClient` compone la URL sobre `TSHOCK_REST_URL`, agrega `token` como parámetro de consulta
   y ejecuta un `GET` con el timeout configurado.
8. La respuesta se lee con un límite de tamaño, se parsea como JSON y se redactan las credenciales
   siempre y las IP según la configuración.
9. `ToolResults` envuelve el resultado y lo devuelve como cadena JSON. El SDK lo envía al anfitrión.
10. El registro de auditoría anota operación, ruta, estado HTTP y duración, sin argumentos.

No hay reintentos automáticos: las operaciones de escritura no son idempotentes y reintentar a
ciegas podría duplicar efectos. El modelo decide si vuelve a intentar.

## Mapeo de errores

Todo código que puede aparecer en `error.code` está en esta tabla. Es un contrato público: los
códigos se agregan, nunca se renombran ni se reutilizan. Los códigos internos del protocolo del
puente y su traducción se documentan en [`bridge.md`](bridge.md#códigos-del-protocolo). La lista se
verifica contra `ErrorCodes.All` en `tests/TShock.MCP.Server.Tests/ErrorCodesTests.cs`.

<!-- BEGIN ERROR CODES -->
| Código | Capa | Cuándo se produce | `httpStatus` | Acción sugerida |
| --- | --- | --- | --- | --- |
| `invalid_argument` | Validación | Falta un argumento obligatorio, llega vacío, tiene un tipo incorrecto o no pasa la validación local (por ejemplo, excede su límite de code points o contiene caracteres de control o formato invisible). No se hizo ninguna petición HTTP. | ausente | Corregir la llamada. |
| `write_disabled` | Política | Se invocó una herramienta de escritura con `TSHOCK_MCP_ALLOW_WRITE` desactivado. | ausente | Habilitar la variable y reiniciar el anfitrión. |
| `rate_limited` | Política | Se agotó el cubo global, el de escritura o el de la tool. No se hizo ninguna petición HTTP. | ausente | Esperar `details.retryAfterMs` y reintentar; no repetir de inmediato. |
| `confirmation_required` | Política | `region_delete` necesita confirmación y el cliente no puede pedirla al operador, ni se envió `confirm: true`. | ausente | Pasar `confirm: true` o usar un cliente con elicitation. |
| `confirmation_declined` | Política | El operador rechazó la confirmación de `region_delete`. | ausente | Ninguna; no se borró nada. |
| `rest_error` | TShock REST | TShock respondió con un estado HTTP de error (401, 403, 404, 500…), o con estado 200 y un campo `error` en el cuerpo. El mensaje es el texto de TShock, saneado. | presente (puede ser `200`) | Revisar token, permisos y disponibilidad del servidor. |
| `network_error` | TShock REST | No se pudo establecer la conexión (DNS, conexión rechazada, TLS). | ausente | Verificar que TShock esté activo y la URL sea correcta. |
| `timeout` | TShock REST | Se superó `TSHOCK_MCP_TIMEOUT_SECONDS` en una llamada a TShock. | ausente | Subir el timeout o revisar la carga del servidor. |
| `cancelled` | Transporte | El anfitrión canceló la operación. | ausente | Ninguna; es una cancelación deliberada. |
| `invalid_response` | Transporte | El cuerpo no es JSON válido o no tiene la forma esperada. | presente | Revisar la versión de TShock, el puente o un proxy intermedio. |
| `response_too_large` | Transporte | La respuesta excede el límite interno de lectura de 5 MB. | presente | Evitar rutas que devuelvan volúmenes masivos. |
| `unexpected_error` | Interno | Cualquier fallo no clasificado. | ausente | Revisar stderr; puede ser un defecto del servidor MCP. |
| `unknown_error` | Interno | Un fallo llegó al sobre sin código. Es un fallback defensivo y no debería observarse. | ausente | Reportar un defecto; nunca debería ocurrir. |
| `bridge_unavailable` | AgentBridge | El puente no está configurado o no se pudo alcanzar. | ausente | Configurar `TSHOCK_BRIDGE_URL` y `TSHOCK_BRIDGE_TOKEN`, o arrancar el plugin. |
| `bridge_timeout` | AgentBridge | El puente no respondió dentro de `TSHOCK_BRIDGE_TIMEOUT_SECONDS`. | ausente | Revisar la carga del servidor o subir el timeout. |
| `bridge_auth_error` | AgentBridge | El puente rechazó el bearer token (HTTP 401). | presente | Revisar `TSHOCK_BRIDGE_TOKEN`. |
| `bridge_error` | AgentBridge | El puente respondió con un error sin traducción específica. | presente | Revisar el plugin y el estado del servidor. |
| `cursor_expired` | AgentBridge | El cursor de eventos ya no está disponible (HTTP 410). | presente | Reanudar desde `details.resumeCursor`. |
| `region_not_found` | AgentBridge | La región pedida no existe. | presente (404) | Corregir el nombre. |
| `region_exists` | AgentBridge | Ya existe una región con ese nombre. | presente (409) | Elegir otro nombre. |
| `owner_not_allowed` | AgentBridge | La política del puente no permite ese propietario. | presente (403) | Usar un propietario permitido. |
<!-- END ERROR CODES -->

Reglas del sobre:

- Éxito: `{"ok":true,"data":<respuesta de TShock>}`. `data` puede ser `null` si TShock responde sin
  cuerpo.
- Error: `{"ok":false,"error":{"code":"...","message":"...","httpStatus":N}}`. `httpStatus` solo
  aparece cuando existe una respuesta HTTP asociada.
- Los mensajes de error se escriben en inglés, son breves y nunca incluyen el token ni cuerpos
  completos de respuesta.

## Rate limiting

El spec 2026-07-28 incluye *«Rate limit tool invocations»* entre las obligaciones **MUST** del
servidor. El gateway lo aplica en el filtro de `Program.cs`, antes del binding de argumentos, con
tres cubos de fichas que se comprueban a la vez:

| Cubo | Alcance | Default | Variable |
| --- | --- | --- | --- |
| Global | Todas las invocaciones | 120/min | `TSHOCK_MCP_RATE_LIMIT_PER_MINUTE` |
| Escritura | Tools con `readOnlyHint: false` | 20/min | `TSHOCK_MCP_WRITE_RATE_LIMIT_PER_MINUTE` |
| Tool | Una tool concreta | Mitad de su clase (`max(1, classRate / 2)`) | Constante de código |

La capacidad del cubo es su tasa por minuto y la recarga es continua (`tasa / 60` por segundo), así
que una ráfaga puede consumir como máximo un minuto de presupuesto. El cubo por tool impide que una
sola herramienta monopolice su clase: con los defaults, una lectura puede consumir hasta 60/min y
una escritura hasta 10/min, aunque el cupo global siga disponible. Es una política de *fair share*
de código, no una tercera variable de entorno.

La adquisición es **atómica**: la llamada consume un token de todos los cubos aplicables o de
ninguno. Si el cubo de escritura deniega, el global no se descuenta; de lo contrario un bucle de
escrituras agotaría el presupuesto de lectura sin haber llegado a TShock. Cuando más de un cubo está
vacío, `retryAfterMs` es el mayor de los tiempos de espera, no el primero que se encuentre.

Se cuenta **toda** invocación `tools/call`, incluidas las que después fallan por argumentos
inválidos, las que devuelven `write_disabled` y los reintentos de *multi round-trip* (por ejemplo, la
confirmación de `region_delete`). Una tool inexistente no crea un cubo por tool: el SDK no empareja
ningún primitivo y la llamada solo consume el cubo global, lo que evita un diccionario que crezca con
nombres controlados por el cliente. El catálogo actual no usa tools respaldadas por tareas; si se
agregaran, la comprobación debe moverse a `CallToolWithAlternateFilters` para contar la creación de
la tarea y no solo su ejecución.

La configuración es estricta: entero entre `1` y `60000`, sin `0`. Un valor ausente usa el default;
uno no numérico, negativo o fuera de rango impide el arranque y el mensaje nombra la variable. No hay
interruptor de apagado porque el límite es una obligación del protocolo, no una comodidad.

El error es aditivo y conserva el sobre:

```json
{"ok":false,"error":{"code":"rate_limited","message":"The tool invocation rate limit was exceeded. Retry after 59943 ms.","details":{"retryAfterMs":59943}}}
```

`isError` es `true` porque la llamada se rechaza antes de ejecutar, igual que `invalid_argument`. No
hay `httpStatus` porque no hubo respuesta HTTP. El rechazo se registra en `LogDebug` con tool, ámbito
y espera, nunca con argumentos ni token: un bucle denegado no debe convertirse en una inundación de
`stderr`.

Límites del propio límite: es por proceso, en memoria, se reinicia con el proceso y no es una cuota
multiinquilino ni distribuida. Tampoco acota llamadas **en vuelo**; un token bucket limita la tasa,
no la concurrencia. El *bulkhead* de concurrencia queda como trabajo futuro. El bucket REST de TShock
(`RESTMaximumRequestsPerInterval`) protege la emisión de tokens de login, no las llamadas con un
token de aplicación; por eso el gateway necesita su propio presupuesto.

## Decisiones de diseño

- **GET para todo.** La API REST de TShock usa GET incluso para mutaciones. El servidor MCP no
  convierte estas llamadas en POST: refleja el contrato real de TShock.
- **Token por variable de entorno.** Evita archivos de configuración versionados y permite que el
  anfitrión lo inyecte en el entorno del proceso hijo.
- **Un único punto de salida.** Toda respuesta pasa por `ToolResults`, lo que mantiene el contrato
  estable para cualquier anfitrión.
- **Validación al arrancar.** Una URL no absoluta o con consulta, fragmento o credenciales
  embebidas, un token ausente, un timeout no numérico o fuera de `1..300`, o HTTP sin cifrar hacia un
  host remoto impiden el arranque con un mensaje claro en stderr. La configuración nunca se corrige
  ni se completa en silencio.
- **El servidor no interpreta el contenido.** No resume, no filtra por heurísticas y no ejecuta nada
  de lo que devuelve TShock; solo redacta IP cuando corresponde.

## Fuera de alcance en la Fase 0

- Transporte HTTP y OAuth 2.1 (Fase 2).
- Puente con TShock para regiones, eventos y descubrimiento de comandos (Fase 1). Ya existe un
  plugin separado con regiones y eventos; el gateway todavía no lo consume. Su contrato está en
  [bridge.md](bridge.md).
- Ejecución de comandos arbitrarios o `rawcmd`.
- Apagado o reinicio del servidor de Terraria.
- Alta, baja o modificación de usuarios, grupos y prohibiciones.
- WebSockets, notificaciones push o eventos en tiempo real. El puente de Fase 1 expone consulta por
  cursor, no push.
- Persistencia propia: el servidor MCP no guarda estado entre ejecuciones, salvo el registro de
  auditoría si se configura.
- Reintentos, caché de respuestas y agregación de varias llamadas.

Cualquier propuesta que cruce estos límites debe discutirse como cambio de fase, no como parche
dentro de la Fase 0. El plan de las fases siguientes y el contexto para retomar el proyecto están en
[roadmap.md](roadmap.md).
