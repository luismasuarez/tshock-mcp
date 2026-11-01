# Puente `TShock.AgentBridge` — Fase 1

El puente es un plugin de TShock que expone por HTTP lo que la API REST no cubre: regiones y
eventos del servidor. Corre **dentro** del proceso de TShock y escucha en loopback; el gateway MCP
sigue siendo el único proceso que habla MCP.

> **Estado.** Regiones (`GET`, `GET` por nombre, `POST`, `DELETE`) y eventos con cursor
> (`GET /v1/events`) están implementados y verificados contra TShock 6.2.1.0. El gateway consume
> `GET /v1/events`, `GET /v1/regions` y `GET /v1/regions/{name}` con `events_since`, `regions_list` y
> `region_info`; `POST /v1/regions` y `DELETE /v1/regions/{name}` con `region_create` y
> `region_delete`. El descubrimiento de comandos y la ejecución con allowlist todavía no existen.
>
> **Este documento describe la capa HTTP del plugin.** Todas las rutas de eventos y regiones son
> invocables por el agente a través de sus cinco tools; solo las de comandos siguen siendo capa
> inferior. Las pruebas por `curl` no son la aceptación MCP;
> consulte [`testing.md`](testing.md).

## Despliegue

El plugin se compila como un único ensamblado `net9.0` y se copia al directorio de plugins
adicionales de la imagen de TShock:

```bash
dotnet build src/TShock.AgentBridge -c Release

cp src/TShock.AgentBridge/bin/Release/net9.0/TShock.AgentBridge.dll \
   "$HOME/.local/share/tshock-mcp/plugins/"

docker restart tshock-test
docker logs tshock-test | grep AgentBridge   # "initiated."
```

No hay recarga en caliente: un cambio del plugin exige reiniciar el contenedor. El arranque es
defensivo: si falta el token, el plugin registra el problema, no arranca el servidor HTTP y deja
TShock funcionando.

### Token del puente

El token es **distinto** del token REST de TShock y se lee, por orden, de:

1. `TSHOCK_BRIDGE_TOKEN` (variable de entorno).
2. El archivo indicado por `TSHOCK_BRIDGE_TOKEN_FILE`.
3. `/run/secrets/bridge-token` por defecto.

Se prefiere el archivo montado como secreto de solo lectura para que el token no aparezca en
`docker inspect`. El plugin no registra el token en ningún caso.

### Puerto

El puerto por defecto es `7879`. `TSHOCK_BRIDGE_PORT` permite cambiarlo con un entero entre 1 y
65535; un valor inválido se ignora y se usa el puerto por defecto.

Dentro del contenedor el `HttpListener` se enlaza a `0.0.0.0` porque el gateway corre en el
anfitrión y llega por un puerto publicado. **El puerto debe publicarse solo en `127.0.0.1`:**

```bash
-p 127.0.0.1:7879:7879
```

## Autenticación

Toda petición debe incluir:

```
Authorization: Bearer <bridge-token>
```

La comparación es en tiempo constante. Una petición sin cabecera o con un token distinto recibe
`401` con `{"error":"unauthorized"}`; el token no se refleja en la respuesta.

## Endpoints

| Método | Ruta | Estado |
| --- | --- | --- |
| `GET` | `/v1/health` | Implementado |
| `GET` | `/v1/world` | Implementado |
| `GET` | `/v1/regions` | Implementado |
| `GET` | `/v1/regions/{name}` | Implementado |
| `POST` | `/v1/regions` | Implementado |
| `DELETE` | `/v1/regions/{name}` | Implementado |
| `GET` | `/v1/events?cursor=&limit=` | Implementado |
| `GET` | `/v1/commands` | Futuro |
| `GET` | `/v1/config` | Futuro |
| `POST` | `/v1/commands/execute` | Futuro |

`/v1/health` devuelve la versión del plugin, el mundo, el contador de ticks del dispatcher y el
estado del buffer de eventos:

```json
{
  "status": "ok",
  "plugin": "TShock.AgentBridge",
  "version": "0.1.0",
  "world": "Test",
  "port": 7879,
  "ticks": 18,
  "events": {
    "session": "1308e7d33bbf4561a5c990d6c56cfbd2",
    "head": 2,
    "oldest": 1,
    "retained": 2
  }
}
```

## Eventos y cursor

El plugin mantiene un **ring buffer en memoria de 10 000 eventos**. Cada evento recibe un cursor
`long` monótono que empieza en `1` y nunca se reutiliza dentro de una sesión del plugin. Los
clientes guardan el último cursor que consumieron y piden los eventos posteriores; no hay estado
por conexión ni notificaciones push.

### Petición

```
GET /v1/events?cursor=12&limit=100
```

| Parámetro | Obligatorio | Valor por defecto | Reglas |
| --- | --- | --- | --- |
| `cursor` | No | El anterior al evento más antiguo retenido | Entero `>= 0`. Es un límite **exclusivo**: devuelve eventos con `cursor` mayor. |
| `limit` | No | `100` | Entero entre `1` y `500`. |

Omitir `cursor` devuelve todo el backlog retenido. Es la forma de arrancar un cliente nuevo incluso
si el buffer ya dio la vuelta. Un `cursor=0` explícito solo es válido mientras el evento más antiguo
siga siendo el `1`; después de que el ring descarte eventos, `0` queda expirado.

Un `cursor` o un `limit` mal formados, negativos o fuera de rango devuelven `400` con
`invalid_cursor` o `invalid_limit`, sin tocar el estado del juego.

### Respuesta

```json
{
  "events": [
    {
      "cursor": 1,
      "at": "2026-11-01T17:01:59.554Z",
      "type": "region.created",
      "untrusted": true,
      "data": { "region": "spawn", "owner": "mcp-gateway" }
    }
  ],
  "cursor": 1,
  "head": 2,
  "oldest": 1,
  "limit": 100,
  "hasMore": true,
  "session": "1308e7d33bbf4561a5c990d6c56cfbd2"
}
```

| Campo | Significado |
| --- | --- |
| `cursor` | Último cursor incluido en la página. Si no hubo eventos nuevos, repite el cursor pedido; es el valor para la siguiente llamada. |
| `head` | Último cursor asignado en esta sesión. |
| `oldest` | Cursor del evento más antiguo retenido. |
| `hasMore` | `true` si quedan eventos posteriores a `cursor`. |
| `session` | Identificador aleatorio de esta sesión del plugin. Cambia al reiniciar. |

### Cursor expirado o desconocido

El endpoint responde `410` con un único código, `cursor_expired`, y un `reason` que distingue dos
casos:

```json
{
  "error": "cursor_expired",
  "reason": "evicted",
  "oldest": 9501,
  "head": 10020,
  "resumeCursor": 9500,
  "session": "1308e7d33bbf4561a5c990d6c56cfbd2"
}
```

| `reason` | Cuándo | Qué debe hacer el cliente |
| --- | --- | --- |
| `evicted` | El cursor es anterior al evento más antiguo retenido: el ring lo descartó. | Reanudar desde `resumeCursor` (`oldest - 1`) y aceptar la pérdida. |
| `unknown` | El cursor es mayor que `head`: viene de una sesión anterior o es inventado. | Reanudar desde `resumeCursor`; si el buffer ya dio la vuelta, se recibe el backlog retenido. |

`resumeCursor` siempre es `oldest - 1`, es decir, el límite exclusivo que permite leer todo lo que
sigue en memoria. Después de un reinicio del contenedor los cursores vuelven a empezar en `1` y
`session` cambia; un cursor viejo cae en `unknown`, nunca se interpreta como «no hay eventos».

### Tipos de evento

| Tipo | Origen | `untrusted` | `data` |
| --- | --- | --- | --- |
| `player.join` | `NetGreetPlayer` | Sí | `player` |
| `player.leave` | `ServerLeave` + roster del puente | Sí | `player` |
| `chat` | `PlayerHooks.PlayerChat` | Sí | `player`, `text` (máx. 512) |
| `command` | `PlayerHooks.PlayerCommand` | Sí | `player`, `command` (solo el nombre) |
| `player.death` | `GetDataHandlers.KillMe` | Sí | `player`, `pvp` |
| `boss.kill` | `ServerApi.Hooks.NpcKilled` | No | `npc`, `netId` |
| `region.created` | `RegionHooks.RegionCreated` | Sí | `region`, `owner` |
| `region.deleted` | `RegionHooks.RegionDeleted` | Sí | `region` |

Notas:

- `player.join` se emite en el saludo (`NetGreetPlayer`), cuando el nombre ya es definitivo, no en
  `PlayerPostLogin`, que solo cubre cuentas autenticadas.
- TShock vacía `Players[who]` en su propio manejador de `ServerLeave`, que corre antes que el del
  puente. Para no perder el nombre, el puente guarda un roster al saludar y lo siembra con los
  jugadores ya conectados cuando el plugin se recarga.
- `command` registra **solo el nombre del comando**. Los argumentos se descartan a propósito:
  `/login` y `/register` llevan contraseñas y `/msg` lleva texto privado. Tampoco se registran los
  comandos de consola (`RealPlayer == false`).
- `boss.kill` no se marca como no confiable porque solo contiene datos del juego; si en el futuro
  incluye el nombre del jugador que asestó el golpe, deberá marcarse.
- `region.created` y `region.deleted` provienen de los hooks de `RegionManager`, así que cubren
  tanto las mutaciones hechas por el puente como las hechas por jugadores en el juego.
  `region.deleted` ignora el caso en que TShock dispara el hook con una región nula.

### Contenido no confiable

`untrusted: true` significa que el payload contiene texto que un jugador puede influir: nombres,
chat, nombres de región, propietario o nombre de comando. El modelo debe tratarlo como **datos**,
nunca como instrucciones. El gateway no filtra ni reescribe ese texto; lo entrega tal cual, acotado
en longitud.

Los eventos no incluyen direcciones IP. La redacción de IP del gateway REST no hace falta aquí
porque el puente no expone el campo.

### Retención y límites

- Capacidad fija: 10 000 eventos. Al desbordarse se descarta el más antiguo.
- Página máxima: 500 eventos. El texto de chat se trunca a 512 caracteres; los nombres a 64, las
  regiones a 128 y los nombres de NPC a 96.
- El buffer vive en memoria: un reinicio lo vacía. Para retención histórica hace falta un sumidero
  externo (JSONL), que no forma parte de esta fase.
- El truncado no parte pares sustitutos, de modo que el JSON sigue siendo válido con nombres que
  contienen caracteres fuera del plano básico.

## Regiones

Las rutas de regiones ejecutan toda la lógica en el hilo del juego a través de
`MainThreadDispatcher`:

- `GET /v1/regions` lista las regiones del mundo actual leyendo `TShock.Regions.Regions`; no usa
  `RegionManager.ListAllRegions`, que devuelve objetos incompletos.
- `GET /v1/regions/{name}` devuelve el registro completo o `404`.
- `POST /v1/regions` acepta `name`, `x`, `y`, `width`, `height` y `owner` opcional. Valida tamaño,
  posición, límites del mundo y **la política de propietario** (ver abajo). Responde `201` con la
  región creada, `400` (`name_required`, `invalid_size`, `invalid_position`, `out_of_bounds`,
  `invalid_json`), `403` (`owner_not_allowed`) o `409` (`region_exists`).
- `DELETE /v1/regions/{name}` comprueba primero que la región exista, porque
  `RegionManager.DeleteRegion` devuelve `true` aunque no haya fila. Responde `204` sin cuerpo cuando
  borra y `404` (`region_not_found`) cuando no existe.

### Política de propietario

El admin del servidor decide quién puede ser dueño de una región. El plugin lee dos variables:

| Variable | Default | Efecto |
| --- | --- | --- |
| `TSHOCK_BRIDGE_REGION_OWNER` | `mcp-gateway` | Propietario usado cuando la petición no pide otro |
| `TSHOCK_BRIDGE_REGION_OWNERS` | vacío | Nombres que el admin permite asignar, separados por coma o punto y coma. Comparación sin distinguir mayúsculas |

Reglas:

- Un `owner` fuera de la lista permitida devuelve `403 owner_not_allowed` y **no** crea la región.
- Entradas inválidas (vacías, de más de 64 caracteres o con caracteres de control) se ignoran: un
  nombre mal escrito no impide arrancar el servidor.
- La política se aplica en el plugin, así que cubre a cualquier cliente del puente, no solo al
  gateway MCP. Un `mcp-gateway` inexistente como cuenta es intencional: marca la procedencia y no
  concede permisos a ningún jugador.

Los nombres de región se decodifican con `Uri.UnescapeDataString`.

**Casing.** Todo el puente emite camelCase: los eventos lo declaran con `JsonPropertyName` y los
registros de región lo obtienen de la política del serializador. Un `RegionDto` se ve así:

```json
{"id": 3, "name": "spawn", "x": 100, "y": 100, "width": 20, "height": 20,
 "owner": "mcp-gateway", "disableBuild": true, "z": 0,
 "allowedIds": [], "allowedGroups": []}
```

`tests/TShock.AgentBridge.Tests/SerializationContractTests.cs` fija este contrato: sin esa prueba, un
cambio de serialización puede devolver PascalCase al gateway sin que nada falle.

## Lo que todavía no existe

- `GET /v1/commands` y la allowlist de ejecución.
- `POST /v1/commands/execute`.
- `GET /v1/config`.
- Las tools de comandos (`commands_list`, `config_get`, `run_command`). Las de eventos y regiones ya
  existen.
- Notificaciones push o streaming. El cliente consulta con su cursor.
- Persistencia de eventos entre reinicios.

## Cómo lo consume el gateway

`events_since`, `regions_list` y `region_info` llaman al puente a través de
`TShock/BridgeClient.cs`, que se autentica con `Authorization: Bearer`, nunca pone el token en la URL
y traduce las respuestas a los códigos estables del sobre MCP (`bridge_unavailable`, `bridge_timeout`,
`bridge_auth_error`, `bridge_error`, `cursor_expired`, `region_not_found`). La configuración del
gateway se documenta en el [README](../README.md#variables-de-entorno).
