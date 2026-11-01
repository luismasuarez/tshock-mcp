# Guía de prueba local: TShock + OpenCode

Esta guía describe cómo levantar un servidor TShock desechable con Docker y conectar el gateway
`tshock-mcp` a OpenCode **solo para este proyecto**.

Decisiones tomadas para esta prueba:

- **Docker** para TShock: no requiere instalar .NET 9 ni ensuciar el sistema.
- **Registro del MCP solo en el proyecto** (la raíz del repositorio); no global.
- **Solo lectura primero**; la escritura se habilita en una segunda pasada.

Al terminar, el agente podrá consultar el estado del servidor, jugadores, mundo, cuentas, grupos y
prohibiciones. Las regiones y los eventos se prueban por HTTP contra el plugin (§11); su aceptación
MCP queda pendiente hasta que existan las tools. El descubrimiento y la ejecución de comandos siguen
pendientes.

---

## 0. Requisitos y estado esperado

| Requisito | Comprobación |
| --- | --- |
| Docker | `docker --version` |
| Puertos libres | `ss -ltn \| grep -E ':(7777\|7878)\b'` sin resultados |
| Espacio | `df -h "$(docker info --format '{{.DockerRootDir}}')"`; la imagen descomprimida ocupa ~405 MB |
| `openssl` | `openssl version` |
| Binario compilado | `dotnet build -c Release` en el repo |
| OpenCode v2 | `opencode --version` |

Si el disco está justo, `docker system df` muestra lo reclamable. No borres caché sin revisar antes
qué se va a eliminar. Comprueba el sistema de archivos del *Docker root dir*: las capas de la imagen
no se guardan necesariamente en `/home`.

---

## 1. Crear el token y los directorios de datos

El token se guarda fuera del repositorio, con permisos de propietario.

```bash
mkdir -p "$HOME/.config/tshock-mcp"
openssl rand -hex 32 > "$HOME/.config/tshock-mcp/token"
chmod 600 "$HOME/.config/tshock-mcp/token"

DATA="$HOME/.local/share/tshock-mcp"
mkdir -p "$DATA/tshock" "$DATA/worlds" "$DATA/plugins"

TOKEN="$(cat "$HOME/.config/tshock-mcp/token")"
```

Los directorios persistentes quedan en `~/.local/share/tshock-mcp`; el repositorio no se contamina
con mundos, bases de datos ni logs.

---

## 2. Levantar TShock

TShock admite flags de arranque que habilitan REST y crean un token de aplicación sin editar
`config.json`:

- `-rest-enabled true`
- `-rest-port 7878`
- `-rest-token <token>`

El token creado así pertenece al grupo `superadmin`. Es cómodo para probar, pero **no debe usarse en
producción**. Para un servidor real, crea un grupo dedicado con permisos mínimos.

```bash
docker run -d --name tshock-test \
  -p 127.0.0.1:7777:7777 \
  -p 127.0.0.1:7878:7878 \
  -v "$DATA/tshock:/tshock" \
  -v "$DATA/worlds:/worlds" \
  -v "$DATA/plugins:/plugins" \
  ghcr.io/pryaxis/tshock:latest \
  -world /worlds/Test.wld \
  -autocreate 2 \
  -port 7777 \
  -maxplayers 8 \
  -rest-enabled true \
  -rest-port 7878 \
  -rest-token "$TOKEN"
```

La primera ejecución genera un mundo pequeño. Puede tardar uno o dos minutos.

```bash
docker ps --filter name=tshock-test
docker logs --tail 80 tshock-test
```

Espera a que el servidor termine de generar el mundo y empiece a escuchar. Si el contenedor se
detiene, revisa los logs antes de reintentar.

> Los puertos se publican solo en `127.0.0.1`: no quedan expuestos a la red local.
> El token es visible en `docker inspect` porque viaja como argumento. Para una prueba local es
> aceptable; en producción usa un token en `config.json` y un grupo con permisos mínimos.

### Token en `config.json` con grupo dedicado

Con `-rest-token`, la herramienta `reload_config` falla siempre con `500`: al recargar, TShock
vuelve a parsear la línea de comandos y ejecuta `RESTStartupTokens.Add(token, ...)` sobre una clave
que ya existe (`TShock.cs`). Además, el mensaje de error de TShock incluye el token en claro, y el
flag fija el grupo `superadmin` en duro, que es más privilegio del necesario.

El primer arranque se hace con `-rest-token` para generar el mundo y `config.json`. Con ese token
temporal se crea un grupo acotado:

```bash
TOKEN="$(cat "$HOME/.config/tshock-mcp/token")"

curl -sS -G "http://127.0.0.1:7878/v2/groups/create" \
  --data-urlencode "group=mcp-rest" \
  --data-urlencode "parent=" \
  --data-urlencode "permissions=tshock.rest.users.info,tshock.rest.users.view,tshock.rest.groups.view,tshock.rest.bans.view" \
  --data-urlencode "chatcolor=255,255,255" \
  --data-urlencode "token=$TOKEN"
```

Ese conjunto cubre las rutas de lectura que exigen permiso. `status`, `motd`, `rules`, `world/read` y
`players/list` no piden ninguno. Si vas a probar escritura, añade los cinco permisos restantes con
`/v2/groups/update`:

```bash
curl -sS -G "http://127.0.0.1:7878/v2/groups/update" \
  --data-urlencode "group=mcp-rest" \
  --data-urlencode "permissions=tshock.rest.users.info,tshock.rest.users.view,tshock.rest.groups.view,tshock.rest.bans.view,tshock.rest.broadcast,tshock.rest.kick,tshock.rest.kill,tshock.rest.mute,tshock.rest.cfg" \
  --data-urlencode "chatcolor=255,255,255" \
  --data-urlencode "token=$TOKEN"
```

No uses `parent=guest`: heredaría los permisos de juego y ampliaría el conjunto. No omitas
`chatcolor` en `update`: el handler genera un valor con puntos que el modelo rechaza en silencio.

Después, mueve el token a la configuración, ligado al grupo nuevo, y arranca sin los flags de REST:

```bash
docker stop tshock-test && docker rm tshock-test

docker run --rm -v "$DATA/tshock:/tshock" --entrypoint sh \
  ghcr.io/pryaxis/tshock:latest -c "
  sed -i 's/\"RestApiEnabled\": false,/\"RestApiEnabled\": true,/' /tshock/config.json
  sed -i 's|\"ApplicationRestTokens\": {}|\"ApplicationRestTokens\": { \"$TOKEN\": { \"Username\": \"mcp-gateway\", \"UserGroupName\": \"mcp-rest\" } }|' /tshock/config.json
  "

docker run -d --name tshock-test \
  -p 127.0.0.1:7777:7777 \
  -p 127.0.0.1:7878:7878 \
  -v "$DATA/tshock:/tshock" \
  -v "$DATA/worlds:/worlds" \
  -v "$DATA/plugins:/plugins" \
  ghcr.io/pryaxis/tshock:latest \
  -world /worlds/Test.wld -port 7777 -maxplayers 8
```

La clave del diccionario es el token en claro (no un hash); `UserGroupName` debe coincidir
exactamente con un grupo existente. Comprueba que la configuración sigue siendo JSON válido antes de
arrancar, que la lectura responde y que la escritura queda denegada por TShock:

```bash
python3 -c 'import json,os; json.load(open(os.path.expanduser("~/.local/share/tshock-mcp/tshock/config.json")))'
curl -s -o /dev/null -w 'read  %{http_code}\n' "http://127.0.0.1:7878/v2/users/list?token=$TOKEN"
curl -s -o /dev/null -w 'write %{http_code}\n' "http://127.0.0.1:7878/v2/server/broadcast?token=$TOKEN&msg=test"
```

Con este arranque el token no aparece en `docker inspect`, `reload_config` funciona y la escritura
tiene dos barreras: el flag del gateway y los permisos del grupo. Los cambios en
`ApplicationRestTokens` requieren reiniciar; los cambios de permisos del grupo se aplican en la
siguiente petición, sin reiniciar.

### Multijugador desde otra PC de la LAN

El binding de arriba permite conectarse solo desde el host. Para jugar desde otra PC:

1. Publica el puerto del juego en la IP del host y **mantén la REST en loopback**:

   ```bash
   HOST_IP="192.168.1.50"   # IP de LAN del host

   -p "$HOST_IP:7777:7777" \
   -p 127.0.0.1:7878:7878 \
   ```

   `-p 7777:7777` (equivalente a `0.0.0.0`) también funciona, pero expone el puerto a cualquier
   interfaz, incluida Tailscale. Si tu IP es dinámica, el binding fijo deja de servir tras un cambio
   de DHCP.

2. Abre el firewall solo para tu subred:

   ```bash
   sudo ufw allow from 192.168.1.0/24 to any port 7777 proto tcp
   ```

   Ajusta la subred a la tuya.

3. Define una contraseña. El archivo `config.json` pertenece a root, así que edítalo con un
   contenedor efímero:

   ```bash
   PASS="$(cat "$HOME/.config/tshock-mcp/server-password")"
   docker run --rm -v "$DATA/tshock:/tshock" --entrypoint sh \
     ghcr.io/pryaxis/tshock:latest -c \
     "sed -i 's/\"ServerPassword\": \"\"/\"ServerPassword\": \"$PASS\"/' /tshock/config.json"
   ```

   La contraseña se guarda en `~/.config/tshock-mcp/server-password` con permisos `600`. No la
   pongas en el repositorio.

4. Comprueba el resultado:

   ```bash
   curl -fsS "http://127.0.0.1:7878/v2/server/status?token=$TOKEN" | grep -o '"serverpassword":[a-z]*'
   ss -ltn | grep 7777   # debe mostrar la IP de LAN, no 127.0.0.1
   ```

   `"serverpassword":true` confirma que TShock exige la contraseña.

5. En la otra PC: Terraria → Multiplayer → Join via IP → IP del host y puerto `7777`.

   - La versión del cliente debe coincidir exactamente con `serverversion` (por ejemplo `1.4.5.8`).
   - Steam debe estar abierto (DRM), pero no hacen falta invitaciones: la conexión por IP es directa.
   - Una cuenta de Steam no puede estar activa en dos PCs a la vez.
   - El primer jugador que ejecute `/setup <código>` en el chat reclama el grupo `superadmin`. El
     código está en `$DATA/tshock/setup-code.txt` y en los logs del contenedor.

---

## 3. Verificar REST antes de tocar OpenCode

```bash
curl -fsS "http://127.0.0.1:7878/v2/server/status?token=$TOKEN" \
  | python3 -m json.tool

curl -fsS "http://127.0.0.1:7878/world/read?token=$TOKEN" \
  | python3 -m json.tool
```

Debe aparecer el nombre del servidor, las versiones de Terraria y TShock, el mundo y el uptime. Si
esto falla, el problema está en TShock o en el token, no en el gateway.

Prueba también un token incorrecto. La ruta de estado es pública y responde aunque el token falte o
sea inválido, así que el rechazo se comprueba en una ruta protegida:

```bash
curl -s -o /dev/null -w '%{http_code}\n' \
  "http://127.0.0.1:7878/world/read?token=incorrecto"
```

Debe devolver `403`.

---

## 4. Envoltorio para lanzar el MCP sin secretos en la configuración

OpenCode lanza el servidor MCP como proceso hijo. Para no escribir el token en la configuración del
proyecto, usa un pequeño envoltorio que lo lee del archivo con permisos `600`.

El envoltorio ya está en el repositorio: `scripts/run-tshock-mcp.sh`, con permisos de ejecución
versionados. Este es su contenido de referencia:

```bash
#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
binary="$repo/src/TShock.MCP.Server/bin/Release/net8.0/tshock-mcp"

if [[ ! -x "$binary" ]]; then
  echo "tshock-mcp: binary not found at $binary; run 'dotnet build -c Release' first." >&2
  exit 1
fi

if [[ -z "${TSHOCK_TOKEN:-}" ]]; then
  token_file="${TSHOCK_TOKEN_FILE:-$HOME/.config/tshock-mcp/token}"
  if [[ ! -r "$token_file" ]]; then
    echo "tshock-mcp: cannot read token file: $token_file" >&2
    exit 1
  fi
  TSHOCK_TOKEN="$(<"$token_file")"
  export TSHOCK_TOKEN
fi

exec "$binary" "$@"
```

```bash
chmod +x scripts/run-tshock-mcp.sh
```

Reglas del envoltorio:

- No escribe nada en stdout; los errores van a stderr.
- No registra el token.
- Falla con un mensaje claro si el binario o el token no existen.

---

## 5. Registrar el MCP solo en este proyecto

Ejecuta desde la raíz del repositorio, **sin** `--global`:

```bash
cd "$(git rev-parse --show-toplevel)"

opencode mcp add tshock \
  --env TSHOCK_REST_URL=http://127.0.0.1:7878 \
  --env TSHOCK_MCP_ALLOW_WRITE=false \
  -- "$PWD/scripts/run-tshock-mcp.sh"
```

Comprueba qué archivo escribió:

```bash
ls -l opencode.json
opencode mcp list
```

`opencode.json` está en `.gitignore` porque contiene rutas absolutas de la máquina, así que
`git status --short` no lo mostrará. Debe aparecer `✓ tshock connected`. El CLI de OpenCode v2
escribe en `mcp.servers`. Si el comando creó una entrada en la configuración global, bórrala y
repite desde la raíz del proyecto.

Configuración manual equivalente, si prefieres editar el archivo:

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "tshock": {
        "type": "local",
        "command": [
          "<ruta-del-repo>/scripts/run-tshock-mcp.sh"
        ],
        "environment": {
          "TSHOCK_REST_URL": "http://127.0.0.1:7878",
          "TSHOCK_MCP_ALLOW_WRITE": "false"
        }
      }
    }
  }
}
```

No agregues `TSHOCK_TOKEN` a este archivo: lo inyecta el envoltorio.

Si las tools no aparecen en la sesión abierta (OpenCode v2 suele recargar la configuración del
proyecto en caliente):

```bash
opencode service restart
opencode mcp list
```

Las tools se exponen con el nombre que declara el servidor (`server_status`, `players_list`, ...) y
el cliente las prefija con el nombre del servidor: `tshock_server_status`, `tshock_players_list`,
etc. Con Code Mode se agrupan bajo `tools.tshock.*`.

---

## 6. Batería de pruebas: solo lectura

Pide al agente, en este orden:

| Paso | Petición al agente | Resultado esperado |
| --- | --- | --- |
| 1 | «Usa `tshock_server_status` y dime el estado» | Versiones, jugadores, mundo y uptime (el nombre puede venir vacío si el servidor no tiene uno configurado) |
| 2 | «Consulta `tshock_world_info`» | Mundo, tamaño, hora, luna de sangre |
| 3 | «Lista los jugadores conectados» | Lista vacía si no hay nadie |
| 4 | «Muéstrame el MOTD y las reglas» | Texto o respuesta vacía, sin error |
| 5 | «Lista cuentas, grupos y prohibiciones» | JSON con los datos de TShock |
| 6 | «Consulta el grupo `superadmin`» | Permisos del grupo |

Verifica que cada respuesta sea el sobre esperado:

```json
{"ok":true,"data":{...}}
```

### Errores controlados

Con un token incorrecto, el gateway debe devolver `rest_error` con `httpStatus: 403`, no un crash.
La ruta de estado es pública, así que usa una herramienta protegida, por ejemplo
`tshock_world_info`. Puedes probarlo reiniciando el MCP con otra variable de entorno o directamente
con `curl`.

Con la escritura desactivada, pide:

> «Envía un broadcast que diga prueba»

Debe responder:

```json
{"ok":false,"error":{"code":"write_disabled","message":"..."}}
```

y no debe llegar nada al servidor.

---

## 7. Segunda pasada: escritura

Solo después de validar la lectura.

1. Edita la configuración del proyecto y cambia `TSHOCK_MCP_ALLOW_WRITE` a `"true"`.
2. Reinicia el servicio:

```bash
opencode service restart
opencode mcp list
```

3. Prueba en este orden:

| Herramienta | Prueba | Verificación |
| --- | --- | --- |
| `broadcast` | «Envía un mensaje que diga prueba MCP» | Visible en el juego o en los logs |
| `save_world` | «Guarda el mundo» | Archivo `.wld` actualizado y `.bak` creado |
| `reload_config` | «Recarga la configuración» | Respuesta de éxito (requiere el token en `config.json`) |
| `kick_player` | Con un jugador conectado: «Expulsa a X» | El jugador pierde la conexión |
| `kill_player` | «Mata el personaje de X» | El jugador muere, reaparece y sigue conectado |
| `mute_player` | «Silencia a X» | `player_info` muestra `muted: true` |

`kill_player` y `mute_player` requieren un jugador conectado. Para probar la redacción, conecta un
cliente de Terraria y pide `player_info`: la IP debe aparecer como `<redacted>`.

Para comprobar el otro lado del invariante, lanza el envoltorio con `TSHOCK_MCP_SHOW_IPS=true` en
un proceso aparte (no hace falta tocar la configuración del proyecto) y repite `player_info`: debe
devolver la IP real, por ejemplo `192.168.1.50`.

Notas observadas en TShock 6.2.1.0 con cliente 1.4.5.8:

- `save_world` responde antes de que termine la escritura en disco; espera unos segundos antes de
  comparar la fecha del `.wld`.
- `kick_player` devolvió `200` («Player X was kicked»), pero el cliente quedó congelado en lugar de
  volver al menú y TShock mantuvo la sesión como activa. Si ves un jugador fantasma, reinicia el
  contenedor para liberar el cupo.
- `kill_player` devolvió `200` («Player X was killed»); el jugador sigue conectado y reaparece, y
  TShock no registra el evento en `docker logs`.
- `mute_player` devolvió `200` las dos veces y el jugador siguió silenciado: la ruta **fija** el
  estado, no alterna. Verifícalo con `player_info` (`muted: true`). Para revertirlo usa
  `/v2/players/unmute?token=...&player=...` directamente contra TShock; el gateway no expone esa
  ruta en la Fase 0.

Al terminar, vuelve a `TSHOCK_MCP_ALLOW_WRITE=false`. No hace falta reiniciar: OpenCode recarga la
configuración del proyecto y el gateway vuelve a responder `write_disabled`, sin tráfico al
servidor.

---

## 8. Limpieza

```bash
docker stop tshock-test
docker rm tshock-test
```

Los datos quedan en `~/.local/share/tshock-mcp`. Si quieres borrarlos:

```bash
rm -rf "$HOME/.local/share/tshock-mcp"
```

Para quitar el MCP del proyecto, elimina la entrada `tshock` de `mcp.servers` en la configuración
del proyecto. El CLI no tiene subcomando `remove` en esta versión.

El token de `~/.config/tshock-mcp/token` se puede conservar para la próxima prueba o borrar.

---

## 9. Problemas frecuentes

| Síntoma | Causa probable | Solución |
| --- | --- | --- |
| `permission denied` al ejecutar Docker | Usuario sin acceso al socket | Agregar el usuario al grupo `docker` y reiniciar sesión |
| El contenedor termina al arrancar | Mundo inexistente, ruta mal escrita o error de configuración | Revisar `docker logs tshock-test` |
| `Connection refused` en `:7878` | REST no habilitado o contenedor detenido | Verificar `-rest-enabled true`, el puerto y `docker ps` |
| Otra PC de la LAN no conecta al `7777` | Puerto publicado en loopback o UFW bloqueando | Publicar en la IP de LAN y permitir la subred (ver «Multijugador desde otra PC de la LAN») |
| «Password incorrect» / no pasa del menú | Contraseña distinta o `ServerPassword` vacío | Revisar `"serverpassword":true` y la contraseña del archivo `600` |
| «Version mismatch» al conectar | Cliente con otra versión de Terraria | Actualizar el cliente a la `serverversion` del servidor |
| `403` en todas las rutas | Token incorrecto o grupo sin permisos | Revisar `-rest-token` y el grupo asociado |
| El contenedor no puede escribir en los volúmenes | Propietario del directorio | Ajustar propietario o ejecutar con `--user` |
| `tshock` no aparece en `opencode mcp list` | Configuración en el proyecto equivocado o servicio sin reiniciar | Repetir desde la raíz, `opencode service restart` |
| El host rechaza la sesión MCP | El comando escribe en stdout o la ruta no existe | Usar el binario compilado y el envoltorio; revisar logs |
| `write_disabled` | Escritura desactivada | `TSHOCK_MCP_ALLOW_WRITE=true` y reiniciar |
| `reload_config` devuelve `500` | Token inyectado con `-rest-token`; TShock lo reañade al recargar | Mover el token a `config.json` y arrancar sin ese flag (ver §2) |
| Disco lleno | Imágenes y caché de Docker | `docker system df`; limpiar solo lo que se entienda |

---

## 10. Qué valida esta prueba

**Sí valida:**

- Handshake MCP entre OpenCode y el gateway.
- Permisos del token REST de TShock.
- Contrato de salida y mapeo de errores.
- Redacción de IP y credenciales.
- Latencia y comportamiento ante errores.

**No valida:**

- Regiones y eventos del puente: §11 prueba el plugin por HTTP, no por MCP. La aceptación MCP
  queda pendiente hasta que existan `BridgeClient` y `BridgeTools`.
- Descubrimiento y ejecución de comandos (Fase 1).
- Transporte HTTP y OAuth (Fase 2).

El plan de las fases siguientes está en [`roadmap.md`](roadmap.md).

---

## 11. Probar el plugin por HTTP (capa inferior)

Esta sección valida el plugin `TShock.AgentBridge` contra el servidor real. El contrato completo
está en [`bridge.md`](bridge.md).

> **No es la prueba de aceptación de una tool MCP.** Aquí se llama directamente a
> `127.0.0.1:7879` con el token del puente. El gateway todavía no consume el puente. La aceptación
> de `events_since` y de las tools de región se hará por MCP, según
> [`testing.md`](testing.md).

### Desplegar el plugin

```bash
dotnet build src/TShock.AgentBridge -c Release

cp src/TShock.AgentBridge/bin/Release/net9.0/TShock.AgentBridge.dll \
   "$HOME/.local/share/tshock-mcp/plugins/"

docker restart tshock-test
docker logs tshock-test | grep AgentBridge   # "initiated."
```

No hay recarga en caliente. El contenedor debe publicar el puente solo en loopback:
`-p 127.0.0.1:7879:7879`. El token del puente se monta como archivo y es distinto del token REST.

### Autenticar sin exponer el token

Guarde la cabecera en un archivo temporal con permisos `600` para que el token no quede en el
historial del intérprete ni en la lista de procesos:

```bash
umask 077
printf 'header = "Authorization: Bearer %s"\n' \
  "$(cat "$HOME/.config/tshock-mcp/bridge-token")" \
  > /tmp/tshock-bridge-auth.curl
chmod 600 /tmp/tshock-bridge-auth.curl
```

Bórrelo al terminar con `rm /tmp/tshock-bridge-auth.curl`.

### Comprobaciones

```bash
B=http://127.0.0.1:7879

# Estado, mundo y estadísticas del buffer
curl -sS -K /tmp/tshock-bridge-auth.curl "$B/v1/health" | python3 -m json.tool

# Página vacía en un buffer recién iniciado: cursor 0, head 0, oldest 1
curl -sS -K /tmp/tshock-bridge-auth.curl "$B/v1/events" | python3 -m json.tool

# Crear y eliminar una región de prueba; cada operación genera un evento.
# Si la región ya existe de una prueba anterior, bórrela primero o use otro nombre.
curl -sS -K /tmp/tshock-bridge-auth.curl -X POST -H 'Content-Type: application/json' \
  --data '{"name":"bridge-event-test","x":100,"y":100,"width":10,"height":10}' \
  "$B/v1/regions"

curl -sS -K /tmp/tshock-bridge-auth.curl "$B/v1/events" | python3 -m json.tool

curl -sS -o /dev/null -w '%{http_code}\n' \
  -K /tmp/tshock-bridge-auth.curl -X DELETE "$B/v1/regions/bridge-event-test"

# Paginación y errores
curl -sS -K /tmp/tshock-bridge-auth.curl "$B/v1/events?cursor=0&limit=1" | python3 -m json.tool
curl -sS -K /tmp/tshock-bridge-auth.curl -o /dev/null -w '%{http_code}\n' \
  "$B/v1/events?cursor=abc"                    # 400
curl -sS -K /tmp/tshock-bridge-auth.curl -o /dev/null -w '%{http_code}\n' \
  "$B/v1/events?cursor=999999"                 # 410 cursor_expired
curl -sS -o /dev/null -w '%{http_code}\n' "$B/v1/events"   # 401 sin bearer
```

Resultados esperados:

- `region.created` y `region.deleted` con cursores consecutivos, `untrusted: true` y el nombre de la
  región.
- `limit=1` devuelve un evento y `hasMore: true`.
- `cursor=abc` devuelve `400 invalid_cursor`; un cursor posterior a `head` devuelve
  `410 cursor_expired` con `reason: "unknown"`.
- `401` sin la cabecera `Authorization`.

Los eventos de jugador (`player.join`, `player.leave`, `chat`, `command`, `player.death`,
`boss.kill`) requieren un cliente de Terraria conectado. Sin jugadores, el servidor no los genera y
la prueba se limita a regiones y al contrato del cursor. Con un cliente:

- Al entrar se emite `player.join` y, al salir, `player.leave`.
- Al ejecutar `/setup <código>` se emite `command` con `{"command":"setup"}`: el código no aparece.
  Es la comprobación de que los argumentos se descartan.
- Al hablar se emite `chat` con `untrusted: true`; al morir, `player.death`; al matar un jefe,
  `boss.kill`.
