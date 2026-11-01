# tshock-mcp

Servidor MCP (*Model Context Protocol*) independiente que permite a un anfitrión de IA —Claude
Desktop, VS Code, Cursor u otro cliente compatible— operar un servidor de Terraria con TShock a
través de la API REST de TShock. Se ejecuta como proceso local, habla MCP por *stdio* y expone 18
herramientas de solo lectura y de escritura acotadas, con escritura deshabilitada por defecto.

> **Proyecto comunitario.** No está afiliado, patrocinado ni respaldado por Pryaxis, el equipo de
> TShock ni el equipo de Terraria. TShock y Terraria son marcas de sus respectivos titulares. El
> proyecto no modifica TShock: solo consume su API REST pública.

## Estado

| Fase | Alcance | Estado |
| --- | --- | --- |
| Fase 0 | Transporte stdio, 18 herramientas, configuración por variables de entorno, sin cambios en TShock | **Implementada** |
| Fase 1 | Puente (*bridge*) con TShock: regiones, eventos y descubrimiento de comandos | Futura, no implementada |
| Fase 2 | Transporte HTTP y OAuth 2.1 para despliegues remotos | Futura, no implementada |

Todo lo que no sea Fase 0 es trabajo planificado. En particular, **no** existen hoy soporte HTTP,
OAuth, puente de plugins, eventos, regiones ni ejecución de comandos arbitrarios.

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

Rutas, permisos y ejemplos completos en [docs/tools.md](docs/tools.md).

Toda herramienta devuelve una cadena JSON con un sobre estable:

```json
{"ok":true,"data":{}}
{"ok":false,"error":{"code":"rest_error","message":"...","httpStatus":403}}
```

Los argumentos se validan antes de cualquier petición: si faltan, llegan vacíos o tienen un tipo
incorrecto, la herramienta devuelve `invalid_argument` sin contactar a TShock. Por separado, las
herramientas de escritura devuelven `write_disabled` cuando `TSHOCK_MCP_ALLOW_WRITE` es `false`,
también sin tráfico de red.

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

Suite unitaria, sin red (64 pruebas):

```bash
dotnet test -c Release
```

Prueba de extremo a extremo: arranca un TShock falso en un puerto efímero, lanza el binario real
y habla MCP por stdio.

```bash
dotnet build -c Release
python3 tests/e2e/mcp_stdio_smoke.py
```

La prueba e2e requiere Python 3 y verifica el sobre de salida, la redacción de IP y credenciales, el
bloqueo de escritura y el mapeo de un 403 de TShock. Es multiplataforma: usa hilos y una cola para
leer la salida del proceso, sin depender de `select`.

## Solución de problemas

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

- [docs/architecture.md](docs/architecture.md): arquitectura de la Fase 0, flujo de una petición y
  mapeo de errores.
- [docs/tools.md](docs/tools.md): referencia completa de las 18 herramientas.
- [docs/security.md](docs/security.md): modelo de amenazas y guía para operadores.
- [docs/roadmap.md](docs/roadmap.md): estado, decisiones, fases siguientes y contexto para retomar.
- [AGENTS.md](AGENTS.md): reglas del repositorio para agentes de IA.

## Licencia

GNU General Public License v3.0 o posterior (`GPL-3.0-or-later`). El texto completo está en
[LICENSE](LICENSE).
