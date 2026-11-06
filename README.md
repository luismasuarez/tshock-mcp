# tshock-mcp

Servidor MCP (*Model Context Protocol*) independiente que permite a un anfitrión de IA —Claude
Desktop, VS Code, Cursor u otro cliente compatible— operar un servidor de Terraria con TShock a
través de la API REST de TShock. Se ejecuta como proceso local, habla MCP por *stdio* y expone 18
herramientas de solo lectura y de escritura acotadas, con escritura deshabilitada por defecto.

> **Proyecto comunitario.** No está afiliado, patrocinado ni respaldado por Pryaxis, el equipo de
> TShock ni el equipo de Terraria. TShock y Terraria son marcas de sus respectivos titulares. El
> proyecto no modifica el código de TShock: la Fase 0 consume su API REST pública y la Fase 1
> agrega un plugin externo que usa sus API públicas.

## Estado

| Fase | Alcance | Estado |
| --- | --- | --- |
| Fase 0 | Transporte stdio, 18 herramientas, configuración por variables de entorno, sin cambios en TShock | **Implementada y verificada** |
| Fase 1 | Puente (*bridge*) con TShock: regiones, eventos y descubrimiento de comandos | **En curso:** eventos y regiones (lectura y escritura) disponibles; descubrimiento de comandos pendiente |
| Fase 2 | Transporte HTTP y OAuth 2.1 para despliegues remotos | Futura, no implementada |

La Fase 0 está verificada contra un servidor real (TShock 6.2.1.0, Terraria 1.4.5.8) con un cliente
en la LAN: lectura, las 6 herramientas de escritura, mapeo de errores y redacción de IP en ambos
sentidos. El procedimiento reproducible está en [`docs/local-testing.md`](docs/local-testing.md).

La Fase 1 ya tiene un plugin (`TShock.AgentBridge`) que carga en TShock y expone regiones y eventos
por HTTP en loopback. El gateway consume ese puente con cinco herramientas: eventos con cursor,
lectura de regiones y creación o borrado de regiones con escritura habilitada, confirmación del
humano y política de propietario configurada por el admin. El descubrimiento de comandos todavía no
existe. El contrato del puente está en [`docs/bridge.md`](docs/bridge.md).

**No** existen hoy soporte HTTP, OAuth, descubrimiento de comandos ni ejecución de comandos
arbitrarios.

## Arquitectura

```
+--------------------------+        MCP (JSON-RPC sobre stdio)        +--------------------------+
|  Anfitrión de IA         |  ----------------------------------->   |  tshock-mcp              |
|  (Claude Desktop,        |                                         |  (proceso local .NET 8)  |
|   VS Code, Cursor)       |  <-----------------------------------   |                          |
+--------------------------+        stdout: solo JSON-RPC            |  logs -> stderr          |
                                                                     +------------+-------------+
                                                                                  | HTTPS/HTTP + token
                                                                                  | (API REST de TShock)
                                                                     +------------v-------------+
                                                                     |  Servidor TShock         |
                                                                     |  Terraria + REST API     |
                                                                     +--------------------------+
```

stdout transporta exclusivamente mensajes JSON-RPC del protocolo. Todos los registros van a stderr,
de modo que el canal de datos nunca se contamina.

La Fase 1 agrega un plugin dentro del servidor TShock que expone regiones y eventos por HTTP en
loopback. El gateway todavía no lo consume; el diagrama y el contrato están en
[`docs/bridge.md`](docs/bridge.md).

## Requisitos

- Un servidor de Terraria con **TShock** y la **API REST habilitada**.
- Un **token de aplicación REST** de TShock con los permisos que se detallan en
  [docs/tools.md](docs/tools.md).
- **.NET 8** (SDK para compilar, runtime para ejecutar).
- Para Docker: Docker Engine o Docker Desktop, si se prefiere la imagen de `docker/`.

## Inicio rápido

### 1. Habilitar la API REST en TShock

Edite `config.json` del servidor TShock (`tshock/config.json`) y ajuste:

```json
{
  "RestApiEnabled": true,
  "RestApiPort": 7878
}
```

Reinicie el servidor. La API queda escuchando en el puerto configurado.

### 2. Crear un token de aplicación REST

En el mismo `config.json`, agregue una entrada en `ApplicationRestTokens`. La clave es el token
secreto y el valor describe la identidad y el grupo con el que TShock evaluará los permisos:

```json
{
  "ApplicationRestTokens": {
    "REPLACE_WITH_A_LONG_RANDOM_TOKEN": {
      "Username": "mcp-agent",
      "UserGroupName": "guest"
    }
  }
}
```

Otorgue al grupo únicamente los permisos que necesite. Para un agente de solo lectura alcanza con
los permisos de consulta (`tshock.rest.users.view`, `tshock.rest.users.info`,
`tshock.rest.groups.view`, `tshock.rest.bans.view`). Los permisos de escritura deben agregarse solo
si se habilita la escritura. Guía completa en [docs/security.md](docs/security.md).

### 3. Exportar la configuración

```bash
export TSHOCK_REST_URL="http://127.0.0.1:7878"
export TSHOCK_TOKEN="REPLACE_WITH_A_LONG_RANDOM_TOKEN"
export TSHOCK_MCP_ALLOW_WRITE="false"
```

`TSHOCK_TOKEN` es obligatorio. Si falta, el proceso termina con un error de configuración en stderr
y código de salida distinto de cero.

### 4. Compilar y ejecutar

Desde la raíz del repositorio:

```bash
dotnet build -c Release
dotnet run --project src/TShock.MCP.Server
```

El proceso espera mensajes MCP por stdin; no imprime nada útil en una terminal interactiva. Para
verificar que arranca, expórtelo con la configuración anterior e invóquelo desde su anfitrión MCP.

## Configuración de Claude Desktop

Compile primero y use la ruta absoluta al binario generado. En macOS el archivo es
`~/Library/Application Support/Claude/claude_desktop_config.json`; en Windows,
`%APPDATA%\Claude\claude_desktop_config.json`.

**Opción A (recomendada): binario compilado.**

```json
{
  "mcpServers": {
    "tshock": {
      "command": "/ABSOLUTE/PATH/TO/tshock-mcp/src/TShock.MCP.Server/bin/Release/net8.0/tshock-mcp",
      "args": [],
      "env": {
        "TSHOCK_REST_URL": "http://127.0.0.1:7878",
        "TSHOCK_TOKEN": "REPLACE_WITH_A_LONG_RANDOM_TOKEN",
        "TSHOCK_MCP_ALLOW_WRITE": "false"
      }
    }
  }
}
```

Si el binario nativo no se generó, use `dotnet` como comando y la ruta absoluta al ensamblado:
`"command": "dotnet"` con `"args": ["/ABSOLUTE/PATH/TO/tshock-mcp/src/TShock.MCP.Server/bin/Release/net8.0/tshock-mcp.dll"]`.
En Windows, el binario nativo se llama `tshock-mcp.exe` y la ruta incluye
`bin\Release\net8.0\`.

**Opción B (solo desarrollo): `dotnet run`.**

```json
{
  "mcpServers": {
    "tshock": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/ABSOLUTE/PATH/TO/tshock-mcp/src/TShock.MCP.Server",
        "-c",
        "Release"
      ],
      "env": {
        "TSHOCK_REST_URL": "http://127.0.0.1:7878",
        "TSHOCK_TOKEN": "REPLACE_WITH_A_LONG_RANDOM_TOKEN"
      }
    }
  }
}
```

`dotnet run` puede escribir mensajes de compilación en stdout antes de que arranque el servidor; si
el anfitrión rechaza la sesión por JSON inválido, compile con anticipación y use la opción A.

Reinicie el anfitrión por completo después de editar el archivo de configuración.

### Docker

La imagen se construye desde la raíz del repositorio. El contenedor se ejecuta con `-i --rm`
(stdin interactivo, sin TTY) porque el transporte es stdio.

Cree primero un archivo de variables de entorno, por ejemplo `docker/.env`, que el `.gitignore` del
repositorio ya excluye:

```bash
# docker/.env (creado por el usuario; no se versiona)
TSHOCK_REST_URL=http://127.0.0.1:7878
TSHOCK_TOKEN=REPLACE_WITH_A_LONG_RANDOM_TOKEN
```

```bash
docker build -f docker/Dockerfile -t tshock-mcp:local .
docker run -i --rm --network host \
  --env-file docker/.env \
  tshock-mcp:local
```

El token nunca debe pasarse como valor literal en la línea de comandos: quedaría en el historial
del intérprete y en la lista de procesos. Use `--env-file` o `-e TSHOCK_TOKEN` sin valor. Esta
última forma reenvía la variable desde el entorno de quien invoca, que debe venir de un perfil del
shell, `direnv` o un gestor de secretos, nunca de un `export` escrito en el historial:

```bash
docker run -i --rm --network host -e TSHOCK_TOKEN tshock-mcp:local
```

`--network host` es específico de Linux. En macOS y Windows quite `--network host` y use
`TSHOCK_REST_URL=http://host.docker.internal:7878`; en Linux, si no usa red de host, agregue
`--add-host=host.docker.internal:host-gateway` y use esa misma URL.

**Importante en esa ruta:** `host.docker.internal` no es un host de loopback para el proceso, de
modo que el binario rechaza el HTTP sin cifrar al arrancar. Hay que agregar
`TSHOCK_MCP_ALLOW_INSECURE=true` o la sesión no inicia. Con esa opción el token viaja en claro y
cualquier proceso del puente de Docker puede leerlo: úsela solo en un Docker local de confianza, no
en un host compartido ni en un servidor remoto. La alternativa segura es publicar TShock por HTTPS.

```bash
docker run -i --rm \
  -e TSHOCK_REST_URL="http://host.docker.internal:7878" \
  -e TSHOCK_MCP_ALLOW_INSECURE=true \
  -e TSHOCK_TOKEN \
  tshock-mcp:local
```

El ejemplo completo para Claude Desktop está en
[docker/claude-desktop.example.json](docker/claude-desktop.example.json). Está escrito para Docker
Desktop (macOS y Windows): usa `host.docker.internal` y `TSHOCK_MCP_ALLOW_INSECURE=true`. En Linux
con `--network host` use `http://127.0.0.1:7878` y omita la variable de inseguridad.

## Variables de entorno

| Variable | Valor por defecto | Descripción |
| --- | --- | --- |
| `TSHOCK_REST_URL` | `http://127.0.0.1:7878` | URL base de la API REST de TShock. Debe ser una URL base simple: sin consulta (`?`), sin fragmento (`#`) ni credenciales embebidas (`usuario:clave@`). |
| `TSHOCK_TOKEN` | *(obligatoria)* | Token de aplicación REST de TShock. |
| `TSHOCK_MCP_ALLOW_WRITE` | `false` | Habilita las 6 herramientas de escritura. |
| `TSHOCK_MCP_SHOW_IPS` | `false` | Muestra las IP de los jugadores en lugar de `<redacted>`. |
| `TSHOCK_MCP_ALLOW_INSECURE` | `false` | Permite HTTP sin cifrar hacia un host que no es de loopback. Envía el token en claro. |
| `TSHOCK_MCP_TIMEOUT_SECONDS` | `15` | Tiempo máximo de espera HTTP, entre 1 y 300. Un valor no numérico o fuera de rango impide el arranque. |
| `TSHOCK_MCP_AUDIT_LOG` | *(sin definir)* | Ruta opcional a un registro de auditoría JSONL. |
| `TSHOCK_MCP_ALLOW_ELICITATION` | `true` | Permite que las tools destructivas pidan confirmación al humano por *elicitation*. Con `false` nunca preguntan y exigen el argumento `confirm: true`. Útil cuando el host declara la capacidad pero su UI no muestra el diálogo en un modo concreto. |
| `TSHOCK_BRIDGE_URL` | `http://127.0.0.1:7879` | URL base del plugin `TShock.AgentBridge`. Si **ninguna** variable `TSHOCK_BRIDGE_*` está definida, el puente queda deshabilitado y sus herramientas responden `bridge_unavailable`. |
| `TSHOCK_BRIDGE_TOKEN` | *(sin definir en línea)* | Token del puente. Distinto del token REST de TShock. |
| `TSHOCK_BRIDGE_TOKEN_FILE` | *(sin definir)* | Archivo con el token del puente, preferido sobre la variable en línea para no exponerlo en `docker inspect` o `ps`. |
| `TSHOCK_BRIDGE_TIMEOUT_SECONDS` | `10` | Tiempo máximo de espera del puente, entre 1 y 300. |

El plugin tiene su propia configuración (`TSHOCK_BRIDGE_PORT`, `TSHOCK_BRIDGE_REGION_OWNER`,
`TSHOCK_BRIDGE_REGION_OWNERS`); está documentada en [`docs/bridge.md`](docs/bridge.md).

La configuración es exclusivamente por variables de entorno: no hay archivos de configuración ni
argumentos de línea de comandos. La validación ocurre al arrancar y es estricta: una URL no absoluta,
con consulta, fragmento o credenciales embebidas, un token ausente, un timeout no numérico o fuera
de `1..300`, o HTTP sin cifrar hacia un host remoto terminan el proceso con un mensaje en stderr y
código de salida distinto de cero. No hay valores de reserva silenciosos.

## Herramientas

Lectura, siempre disponibles:

| Herramienta | Propósito |
| --- | --- |
| `server_status` | Estado general del servidor. |
| `world_info` | Metadatos del mundo. |
| `players_list` | Jugadores conectados. |
| `player_info` | Detalle de un jugador conectado. |
| `server_motd` | Mensaje del día. |
| `server_rules` | Reglas del servidor. |
| `users_list` | Cuentas registradas. |
| `user_info` | Detalle de una cuenta. |
| `groups_list` | Grupos de permisos. |
| `group_info` | Detalle de un grupo. |
| `bans_list` | Prohibiciones registradas. |
| `ban_info` | Detalle de una prohibición. |

Escritura, requieren `TSHOCK_MCP_ALLOW_WRITE=true`:

| Herramienta | Propósito |
| --- | --- |
| `broadcast` | Envía un mensaje a todos los jugadores. |
| `kick_player` | Expulsa a un jugador. |
| `kill_player` | Mata a un jugador sin expulsarlo. |
| `mute_player` | Silencia el chat de un jugador. |
| `save_world` | Guarda el mundo en disco. |
| `reload_config` | Recarga la configuración de TShock. |

Puente (Fase 1), solo lectura, con el plugin `TShock.AgentBridge`:

| Herramienta | Propósito |
| --- | --- |
| `events_since` | Eventos del servidor posteriores a un cursor exclusivo: entradas, salidas, chat, comandos, muertes, jefes y cambios de región. Devuelve el cursor para la siguiente llamada. |
| `regions_list` | Regiones del mundo actual con área, propietario y permisos. |
| `region_info` | Una región por nombre exacto; `region_not_found` si no existe. |
| `region_create` | Crea una región protegida. Requiere escritura habilitada; el propietario lo decide la política del servidor. |
| `region_delete` | Borra una región. Requiere escritura habilitada y confirmación del humano (o `confirm: true` si el cliente no puede preguntar). |

Rutas, permisos, argumentos y ejemplos completos en [docs/tools.md](docs/tools.md).

Estas 23 son las únicas herramientas MCP existentes: 18 REST y 5 del puente. Las herramientas de
comandos (`commands_list`, `config_get`, `run_command`) todavía no existen; cuando se implementen, se
agregarán a esta sección y a `docs/tools.md`.

Toda herramienta devuelve una cadena JSON con un sobre estable:

```json
{"ok":true,"data":{}}
{"ok":false,"error":{"code":"rest_error","message":"...","httpStatus":403}}
```

Los argumentos se validan antes de cualquier petición: si faltan, llegan vacíos, tienen un tipo
incorrecto, exceden su límite de code points o contienen caracteres de control o formato invisible,
la herramienta devuelve `invalid_argument` sin contactar a TShock. Cada argumento de texto publica
su `maxLength` en `inputSchema`; los límites están en
[docs/tools.md](docs/tools.md#límites-de-argumentos-de-texto).
Por separado, las herramientas de escritura devuelven `write_disabled` cuando
`TSHOCK_MCP_ALLOW_WRITE` es `false`, también sin tráfico de red. La lista completa y canónica de
códigos está en [docs/architecture.md](docs/architecture.md#mapeo-de-errores).

## Seguridad en resumen

- **Solo lectura por defecto.** Las herramientas de escritura exigen `TSHOCK_MCP_ALLOW_WRITE=true`.
- **El token nunca se registra.** TShock lo exige como parámetro de consulta; el servidor lo omite
  de logs, errores y del registro de auditoría.
- **Loopback por defecto.** Se rechaza HTTP sin cifrar hacia hosts que no son de loopback salvo que
  se active `TSHOCK_MCP_ALLOW_INSECURE=true`. Los despliegues remotos deben usar HTTPS; OAuth 2.1
  llegará en una fase futura.
- **IP redactadas por defecto** (`<redacted>`), salvo que se active `TSHOCK_MCP_SHOW_IPS=true`. Se
  redactan tanto los campos `ip` como cualquier valor con forma de IP (por ejemplo, el `identifier`
  de una prohibición). Los campos `token` y `password` se redactan siempre.
- **Salida no confiable.** Nombres de jugadores, chat y MOTD son datos que provienen de terceros. El
  modelo debe tratarlos como datos, nunca como instrucciones (riesgo de inyección de prompts).
- **Sin comandos arbitrarios.** No hay ejecución de comandos, apagado del servidor ni mutación de
  usuarios o prohibiciones en la Fase 0.

Antes de habilitar escritura, revise [docs/security.md](docs/security.md).

## Pruebas

Suite unitaria, sin red (291 pruebas: 246 del gateway y 45 del puente):

```bash
dotnet test -c Release
```

Prueba de extremo a extremo: arranca un TShock falso y un puente falso en puertos efímeros, lanza
el binario real y habla MCP por stdio.

```bash
dotnet build -c Release
python3 tests/e2e/mcp_stdio_smoke.py
```

La prueba e2e requiere Python 3 y verifica el sobre de salida, la redacción de IP y credenciales, el
bloqueo de escritura, el mapeo de un 403 de TShock, el cursor de `events_since` y la degradación a
`bridge_unavailable` cuando el puente no responde (96 comprobaciones). Es multiplataforma: usa hilos
y una cola para leer la salida del proceso, sin depender de `select`.

Cubre las 18 tools REST y las 5 herramientas del puente, incluida la elicitation. El contrato de
`tools/list` está congelado en `tests/contract/tools-list.snapshot.json` y se verifica en la misma
e2e en tres eras de protocolo y varios estados de configuración. Para revisarlo o regenerarlo:

```bash
python3 tests/contract/tools_list_contract.py --check
python3 tests/contract/tools_list_contract.py --write   # solo tras revisar el diff
```

La matriz de capas y la definición de terminado están en [docs/testing.md](docs/testing.md).

## Solución de problemas

Los códigos de esta tabla son un subconjunto de la
[tabla canónica](docs/architecture.md#mapeo-de-errores).

| Síntoma | Causa probable | Solución |
| --- | --- | --- |
| `network_error` con «Connection refused» | TShock no está en ejecución, `RestApiEnabled` es `false` o el puerto no coincide. | Verifique el proceso, `RestApiEnabled`, `RestApiPort` y `TSHOCK_REST_URL`. |
| `rest_error` con `httpStatus: 403` | Token inválido o el grupo del token no tiene el permiso de la ruta. | Revise `ApplicationRestTokens` y los permisos del grupo en TShock. |
| `write_disabled` | Se invocó una herramienta de escritura sin habilitarla. | Defina `TSHOCK_MCP_ALLOW_WRITE=true` y reinicie el anfitrión MCP. |
| `invalid_argument` | Falta un argumento obligatorio, llegó vacío o con un tipo incorrecto. | Corrija la llamada; no se hizo ninguna petición a TShock. |
| `timeout` | El servidor TShock no responde a tiempo. | Suba `TSHOCK_MCP_TIMEOUT_SECONDS` (máximo 300) y revise la carga del servidor. |
| El anfitrión no inicia la sesión MCP | Algo escribe en stdout o el comando/ruta es incorrecto. | Use la ruta absoluta al binario, compile antes y asegúrese de que solo JSON-RPC va a stdout. |
| Error de configuración sobre HTTP no seguro | `TSHOCK_REST_URL` apunta a un host remoto por HTTP. | Use HTTPS o, con pleno conocimiento del riesgo, `TSHOCK_MCP_ALLOW_INSECURE=true`. |
| Error de configuración sobre `TSHOCK_MCP_TIMEOUT_SECONDS` | El valor no es numérico o está fuera de `1..300`. | Use un entero entre 1 y 300; no hay valor de reserva. |
| Error de configuración sobre `TSHOCK_REST_URL` | La URL no es absoluta o incluye consulta, fragmento o credenciales embebidas. | Use solo un esquema, host, puerto y, si hace falta, una ruta base. |

**Advertencia sobre el token.** El token viaja como parámetro de consulta porque TShock lo exige
así. Puede quedar visible en registros del servidor TShock, proxys inversos o historiales de
comandos. No lo incluya en archivos versionados, prefiera HTTPS y rótelo si sospecha que se filtró.
Un token con permisos de escritura equivale a control operativo del servidor.

## Documentación

- [docs/architecture.md](docs/architecture.md): arquitectura, capas actuales, flujo de una petición
  y mapeo de errores.
- [docs/bridge.md](docs/bridge.md): contrato del puente de Fase 1 (regiones y eventos con cursor).
- [docs/testing.md](docs/testing.md): capas de prueba, comandos y definición de terminado.
- [docs/tools.md](docs/tools.md): referencia completa de las 23 herramientas.
- [docs/security.md](docs/security.md): modelo de amenazas y guía para operadores.
- [docs/roadmap.md](docs/roadmap.md): estado, decisiones, fases siguientes y contexto para retomar.
- [docs/backlog.md](docs/backlog.md): mejoras priorizadas, pensadas para resolverse una por sesión.
- [docs/local-testing.md](docs/local-testing.md): guía paso a paso para probar con TShock y OpenCode.
- [SECURITY.md](SECURITY.md): versiones soportadas y cómo reportar una vulnerabilidad.
- [CONTRIBUTING.md](CONTRIBUTING.md): entorno, reglas de código y proceso de contribución.
- [AGENTS.md](AGENTS.md): reglas del repositorio para agentes de IA.

## Licencia

GNU General Public License v3.0 o posterior (`GPL-3.0-or-later`). El texto completo está en
[LICENSE](LICENSE).
