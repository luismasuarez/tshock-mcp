# Referencia de herramientas

La Fase 0 expone exactamente **18 herramientas REST**: 12 de lectura y 6 de escritura. Todas se
ejecutan como peticiones `GET` contra la API REST de TShock, que usa ese método incluso para
mutaciones.

La Fase 1 agrega de forma aditiva **herramientas del puente** que hablan con el plugin
`TShock.AgentBridge` por HTTP en loopback. Hoy existen cinco: `events_since`, `regions_list`,
`region_info`, `region_create` y `region_delete`, así que el catálogo total es de
**23 herramientas**. Las 18 de la Fase 0 conservan su nombre, sus rutas y su semántica.

Convenciones de la tabla:

- **Permiso**: permiso de TShock que debe tener el grupo asociado al token de aplicación. «Ninguno»
  significa que la ruta no exige permiso a nivel de TShock; el token sigue siendo obligatorio.
- **Efectos**: consecuencia sobre el servidor. Las herramientas de lectura no mutan estado.
- Las herramientas de escritura requieren además `TSHOCK_MCP_ALLOW_WRITE=true`; sin esa variable
  devuelven `write_disabled` sin llegar a TShock.
- **Argumentos**: faltante, vacío (cadena vacía o solo espacios) o de tipo incorrecto produce
  `invalid_argument` sin ninguna petición HTTP. El cliente nunca completa ni adivina valores.

## Anotaciones MCP

Cada herramienta declara los cuatro hints de `annotations` con un booleano explícito. No se deja
ninguno en el valor por defecto del SDK (`destructiveHint` y `openWorldHint` serían `true`, lo que
describiría mal las herramientas de lectura) y el directorio de OpenAI rechaza las herramientas a
las que les falta uno.

- `readOnlyHint`: la herramienta no muta estado.
- `destructiveHint`: puede destruir o quitar algo de forma difícil de revertir. Solo es relevante
  cuando `readOnlyHint` es `false`, pero se declara siempre.
- `idempotentHint`: repetir la llamada con los mismos argumentos no produce efecto adicional.
- `openWorldHint`: el dominio es abierto e impredecible. Es `false` en todo el catálogo porque el
  alcance es un servidor TShock concreto, con jugadores, cuentas y regiones que el servidor conoce.

Las 15 herramientas de lectura comparten la misma combinación: `readOnlyHint: true`,
`destructiveHint: false`, `idempotentHint: true`, `openWorldHint: false`.

Las de escritura:

| Herramienta | `readOnlyHint` | `destructiveHint` | `idempotentHint` | `openWorldHint` |
| --- | --- | --- | --- | --- |
| `broadcast` | `false` | `false` | `false` | `false` |
| `kick_player` | `false` | `true` | `false` | `false` |
| `kill_player` | `false` | `true` | `false` | `false` |
| `mute_player` | `false` | `true` | `false` | `false` |
| `save_world` | `false` | `false` | `true` | `false` |
| `reload_config` | `false` | `false` | `true` | `false` |
| `region_create` | `false` | `false` | `false` | `false` |
| `region_delete` | `false` | `true` | `false` | `false` |

`idempotentHint` se mantiene en `false` para las operaciones destructivas aunque repetirlas no
cambie el estado final: el hint también guía los reintentos automáticos y conviene que un agente no
repita un borrado o una expulsión tras un timeout.

Los valores se verifican en `tests/TShock.MCP.Server.Tests/ToolAnnotationTests.cs` y en la prueba
MCP por stdio.

## Herramientas de lectura (siempre disponibles)

| Herramienta | Propósito | Ruta | Permiso | Argumentos | Efectos |
| --- | --- | --- | --- | --- | --- |
| `server_status` | Estado del servidor: nombre, versiones de Terraria y TShock, puerto, jugadores conectados y máximos, mundo y tiempo en línea. Primera comprobación de salud recomendada. | `GET /v2/server/status` | Ninguno | — | Ninguno |
| `world_info` | Metadatos del mundo: nombre, tamaño en tiles, hora, si es de día, luna de sangre e invasión. | `GET /world/read` | Ninguno | — | Ninguno |
| `players_list` | Jugadores conectados con apodo, cuenta, grupo, estado, código de estado y equipo. No incluye IP. | `GET /v2/players/list` | Ninguno | — | Ninguno |
| `player_info` | Detalle de un jugador conectado: inventario, equipo, buffs, grupo y silencio. La IP se redacta por defecto. | `GET /v4/players/read` | `tshock.rest.users.info` | `player` (nombre exacto en el juego) | Ninguno |
| `server_motd` | Mensaje del día que se muestra al entrar, si está configurado. | `GET /v3/server/motd` | Ninguno | — | Ninguno |
| `server_rules` | Texto de reglas del servidor, si está configurado. | `GET /v3/server/rules` | Ninguno | — | Ninguno |
| `users_list` | Cuentas registradas con id numérico, nombre y grupo. | `GET /v2/users/list` | `tshock.rest.users.view` | — | Ninguno |
| `user_info` | Detalle de una cuenta registrada. | `GET /v2/users/read` | `tshock.rest.users.view` | `user`, `type` (`name` o `id`, por defecto `name`) | Ninguno |
| `groups_list` | Grupos de permisos definidos en TShock. | `GET /v2/groups/list` | `tshock.rest.groups.view` | — | Ninguno |
| `group_info` | Detalle de un grupo: permisos, color y jerarquía. | `GET /v2/groups/read` | `tshock.rest.groups.view` | `group` (nombre del grupo) | Ninguno |
| `bans_list` | Prohibiciones registradas; TShock puede incluir registros históricos, no solo las vigentes. | `GET /v3/bans/list` | `tshock.rest.bans.view` | — | Ninguno |
| `ban_info` | Detalle de una prohibición por número de ticket. | `GET /v3/bans/read` | `tshock.rest.bans.view` | `ticketNumber` | Ninguno |

## Herramientas de escritura (requieren `TSHOCK_MCP_ALLOW_WRITE=true`)

| Herramienta | Propósito | Ruta | Permiso | Argumentos | Efectos |
| --- | --- | --- | --- | --- | --- |
| `broadcast` | Envía un mensaje a todos los jugadores conectados. | `GET /v2/server/broadcast` | `tshock.rest.broadcast` | `message` | Visible para todos los jugadores de inmediato. |
| `kick_player` | Expulsa a un jugador del servidor. | `GET /v2/players/kick` | `tshock.rest.kick` | `player`, `reason` (opcional) | El jugador pierde la conexión; puede reconectarse si no está prohibido. |
| `kill_player` | Mata al personaje de un jugador sin expulsarlo. | `GET /v2/players/kill` | `tshock.rest.kill` | `player`, `from` (opcional) | El jugador muere y reaparece según las reglas del servidor; puede perder objetos si el modo lo permite. |
| `mute_player` | Silencia el chat de un jugador. | `GET /v2/players/mute` | `tshock.rest.mute` | `player` | Los mensajes del jugador dejan de difundirse. La ruta fija el estado y no lo alterna; no hay herramienta para revertirlo (`/v2/players/unmute` queda fuera de la Fase 0). |
| `save_world` | Guarda el mundo actual en disco. | `GET /v2/world/save` | `tshock.rest.cfg` | — | Persiste el estado del mundo; puede producir una pausa breve. |
| `reload_config` | Recarga la configuración de TShock. | `GET /v3/server/reload` | `tshock.rest.cfg` | — | Aplica cambios de configuración sin reiniciar; puede alterar el comportamiento del servidor en caliente. |

No existen herramientas para prohibir jugadores, crear o borrar cuentas, modificar grupos, apagar el
servidor ni ejecutar comandos. Esas operaciones quedan fuera de la Fase 0 a propósito.

Para un token de solo lectura, el grupo dedicado necesita la unión de la columna «Permiso» de las
rutas que exigen uno: `tshock.rest.users.info`, `tshock.rest.users.view`, `tshock.rest.groups.view` y
`tshock.rest.bans.view`. `server_status`, `server_motd`, `server_rules`, `world_info` y
`players_list` funcionan sin permisos. Habilitar escritura añade cinco permisos más; el detalle y los
comandos para crear el grupo están en [`security.md`](security.md#el-token).

## Sobre de salida

Toda herramienta devuelve **una cadena JSON**, no un objeto estructurado. Esto garantiza que todos
los anfitriones MCP rendericen el mismo contenido.

Éxito:

```json
{"ok":true,"data":{ ... }}
```

Error:

```json
{"ok":false,"error":{"code":"rest_error","message":"You do not have permission to use this endpoint.","httpStatus":403}}
```

`httpStatus` solo está presente cuando hubo una respuesta HTTP. El `message` de `rest_error` no es
un texto fijo: es el campo `error` que devuelve TShock, saneado (sin token ni URL base) y truncado si
es muy largo. Cuando TShock responde con un error sin texto utilizable, el mensaje es genérico, por
ejemplo `TShock returned HTTP 403 (Forbidden).`

TShock también informa errores **con estado 200 y un campo `error` en el cuerpo**. Esos casos se
mapean igual a `rest_error`, con `httpStatus: 200`, para que el modelo nunca trate como éxito una
respuesta que TShock marcó como fallida.

Códigos posibles:

| Código | Significado |
| --- | --- |
| `write_disabled` | Herramienta de escritura invocada con la escritura deshabilitada. |
| `invalid_argument` | Argumento faltante, vacío o de tipo incorrecto; se devuelve sin llamar a TShock. |
| `rest_error` | TShock respondió con un estado HTTP de error, o con estado 200 y un campo `error` en el cuerpo. |
| `network_error` | Fallo de conexión, DNS o TLS. |
| `timeout` | Se superó el timeout configurado. |
| `cancelled` | La operación fue cancelada por el anfitrión. |
| `invalid_response` | La respuesta no es JSON válido o no tiene la forma esperada. |
| `response_too_large` | La respuesta supera el límite interno de lectura. |
| `unexpected_error` | Fallo no clasificado. |

## Ejemplos

Los valores de `data` son ilustrativos; la forma exacta depende de la versión de TShock.

### Lectura: `server_status`

Llamada MCP (conceptual):

```json
{"name":"server_status","arguments":{}}
```

Resultado:

```json
{
  "ok": true,
  "data": {
    "name": "Terraria Server",
    "serverversion": "1.4.4.9",
    "tshockversion": "5.2.0.0",
    "port": 7777,
    "playercount": 2,
    "maxplayers": 8,
    "world": "MyWorld",
    "uptime": "01:23:45"
  }
}
```

### Lectura: `player_info`

```json
{"name":"player_info","arguments":{"player":"Alice"}}
```

```json
{
  "ok": true,
  "data": {
    "nickname": "Alice",
    "username": "alice",
    "group": "default",
    "ip": "<redacted>",
    "muted": false,
    "team": 0
  }
}
```

El campo `ip` aparece como `<redacted>` salvo que se configure `TSHOCK_MCP_SHOW_IPS=true`.
`players_list` nunca incluye IP.

### Escritura: `broadcast`

```json
{"name":"broadcast","arguments":{"message":"Server restart in 5 minutes."}}
```

Con la escritura habilitada y permisos correctos:

```json
{"ok":true,"data":null}
```

`data` puede ser `null` porque TShock responde sin cuerpo a algunas rutas de acción.

Con la escritura deshabilitada, la petición no sale del proceso:

```json
{
  "ok": false,
  "error": {
    "code": "write_disabled",
    "message": "Write tools are disabled. Set TSHOCK_MCP_ALLOW_WRITE=true to enable them."
  }
}
```

### Argumento faltante o inválido

Una llamada sin el argumento obligatorio, con cadena vacía o con un tipo incorrecto no produce
ninguna petición HTTP. En el ejemplo se supone la escritura habilitada, para que no intervenga
`write_disabled`:

```json
{"name":"kick_player","arguments":{"reason":"AFK"}}
```

```json
{
  "ok": false,
  "error": {
    "code": "invalid_argument",
    "message": "'player' must not be empty."
  }
}
```

### Error de permisos

Si el token existe pero su grupo no tiene `tshock.rest.kick`, TShock responde `403` y el mensaje
conserva el texto que envía TShock (en este ejemplo, su aviso de permiso denegado):

```json
{
  "ok": false,
  "error": {
    "code": "rest_error",
    "message": "You do not have permission to use this endpoint.",
    "httpStatus": 403
  }
}
```

Si TShock no incluye un campo `error` legible, el mensaje degrada a
`TShock returned HTTP 403 (Forbidden).` En ambos casos el texto se sanea: nunca contiene el token ni
la URL base. Un `error` con estado 200 produce el mismo código con `httpStatus: 200`.

## Redacción y datos no confiables

- La redacción se aplica de forma recursiva a los campos `ip`, `token` y `password` de las respuestas.
  Los campos `token` y `password` se redactan siempre; las IP solo cuando `TSHOCK_MCP_SHOW_IPS` es
  `false` (el valor por defecto). También se redacta cualquier **valor** con forma de IP, aunque su
  clave no sea `ip`: por ejemplo, el `identifier` de una prohibición por dirección IP en
  `/v3/bans/*`. Es una medida de privacidad y de defensa en profundidad, no un control de acceso.
- Los nombres de jugadores, el chat, el MOTD y las reglas son **entrada no confiable**: cualquier
  persona conectada puede escribirlos. El modelo debe tratarlos como datos y nunca como
  instrucciones. Consulte [security.md](security.md#inyección-de-prompts).
- El tamaño de las respuestas está acotado; una respuesta excesiva se rechaza con
  `response_too_large` en lugar de consumir memoria sin límite.

## Herramientas del puente (Fase 1)

Estas herramientas no usan la API REST: hablan con el plugin `TShock.AgentBridge` por HTTP en
loopback, con un token propio distinto del token REST. Su contrato HTTP está en
[bridge.md](bridge.md).

| Herramienta | Propósito | Ruta | Argumentos | Efectos |
| --- | --- | --- | --- | --- |
| `events_since` | Eventos del servidor posteriores a un cursor **exclusivo**: entradas, salidas, chat, comandos, muertes, jefes y cambios de región. | `GET /v1/events` | `cursor` (opcional, entero `>= 0`), `limit` (opcional, 1–500, 100 por defecto) | Ninguno. Solo lectura |
| `regions_list` | Regiones del mundo actual con área, propietario, protección y listas de usuarios y grupos permitidos. | `GET /v1/regions` | — | Ninguno. Solo lectura |
| `region_info` | Una región por su nombre exacto. Devuelve `region_not_found` si no existe. | `GET /v1/regions/{name}` | `name` (nombre exacto, máximo 128 caracteres, sin `/` ni caracteres de control) | Ninguno. Solo lectura |
| `region_create` | Crea una región protegida. El propietario por defecto lo fija el servidor; otro solo si el admin lo permite. | `POST /v1/regions` | `name`, `x`, `y`, `width`, `height`, `owner` (opcional) | Aparece una región nueva y se emite `region.created`. |
| `region_delete` | Borra una región y su protección. **Irreversible.** | `DELETE /v1/regions/{name}` | `name`, `confirm` (solo para clientes sin elicitation) | Desaparece la región y se emite `region.deleted`. |

Comportamiento:

- **Cursor.** El valor devuelto en `cursor` es el que se pasa en la siguiente llamada; la respuesta
  solo incluye eventos con cursor mayor. Omitirlo devuelve todo el backlog retenido, lo que permite
  arrancar un cliente nuevo.
- **Paginación.** `hasMore` indica si quedan eventos posteriores. El límite máximo por llamada es
  500.
- **Errores.** `bridge_unavailable` (puente apagado o sin configurar), `bridge_timeout`,
  `bridge_auth_error`, `bridge_error`, `cursor_expired`, `region_not_found`, `region_exists`,
  `owner_not_allowed`, `confirmation_required`, `confirmation_declined` y `write_disabled`. `cursor_expired`
  agrega un objeto `details` dentro de `error` con `reason` (`evicted` o `unknown`), `oldest`,
  `head`, `resumeCursor` y `session`, para que el agente reanude sin adivinar. `region_not_found`
  solo significa que el nombre pedido no existe: un fallo del puente sigue siendo `bridge_error`.
- **Sin puente configurado**, la herramienta sigue en el catálogo y responde `bridge_unavailable`.
  Un agente que no ve la herramienta no puede diagnosticar nada; un error explicado sí.
- **Contenido no confiable.** Los eventos con texto que un jugador puede influir traen
  `"untrusted": true`. El modelo debe tratarlos como datos, nunca como instrucciones.
- No depende de `TSHOCK_MCP_ALLOW_WRITE`: es de solo lectura.

### Escritura de regiones

- Ambas tools comprueban `TSHOCK_MCP_ALLOW_WRITE=true` en runtime; sin él devuelven `write_disabled`
  y no llegan al puente.
- **Propietario.** `region_create` sin `owner` usa el propietario configurado por el admin en el
  plugin. Cualquier otro nombre debe estar en `TSHOCK_BRIDGE_REGION_OWNERS`; si no, el puente responde
  `owner_not_allowed` (403). La política se aplica en el servidor, así que también cubre a otros
  clientes del puente.
- **Confirmación.** `region_delete` pide confirmación al humano mediante *elicitation* si el cliente
  declara esa capacidad. Si el cliente no puede preguntar, exige `confirm: true`; sin él devuelve
  `confirmation_required`. Si el humano rechaza, devuelve `confirmation_declined` y no borra nada.
  `TSHOCK_MCP_ALLOW_ELICITATION=false` desactiva la pregunta por completo y deja siempre el camino de
  `confirm: true`; es la salida para hosts que declaran la capacidad pero no muestran el diálogo.

## Catálogo actual y herramientas pendientes

Las 23 herramientas de las secciones anteriores son las únicas que el agente puede invocar hoy: 18
contra la API REST y 5 contra el puente.

Pendientes de implementar:

| Tool prevista | Origen | Tipo | Estado |
| --- | --- | --- | --- |
| `commands_list` | `GET /v1/commands` del puente | Lectura | No existe |
| `config_get` | `GET /v1/config` del puente | Lectura | No existe |
| `run_command` | `POST /v1/commands/execute` | Escritura con allowlist | No existe |

Cuando se implementen:

- Usarán el sobre estable `ok` / `error` y el mismo filtro de argumentos.
- Las de escritura comprobarán `TSHOCK_MCP_ALLOW_WRITE=true`; `region_delete` exigirá confirmación.
- `events_since` devolverá `cursor`, `head`, `oldest`, `hasMore` y `session`, y traducirá
  `cursor_expired` a un error con `resumeCursor`.
- Cada una tendrá prueba MCP por stdio, no solo `curl` al puente. Consulte
  [testing.md](testing.md).

El contrato HTTP del plugin, incluido el cursor, está en [bridge.md](bridge.md).
