# Guía de prueba local: TShock + OpenCode

Esta guía describe cómo levantar un servidor TShock desechable con Docker y conectar el gateway
`tshock-mcp` a OpenCode **solo para este proyecto**.

Decisiones tomadas para esta prueba:

- **Docker** para TShock: no requiere instalar .NET 9 ni ensuciar el sistema.
- **Registro del MCP solo en el proyecto** `/home/luisma/dev/hobbies/tshock-mcp`; no global.
- **Solo lectura primero**; la escritura se habilita en una segunda pasada.

Al terminar, el agente podrá consultar el estado del servidor, jugadores, mundo, cuentas, grupos y
prohibiciones. Las regiones, eventos y comandos quedan para la Fase 1.

---

## 0. Requisitos y estado esperado

| Requisito | Comprobación |
| --- | --- |
| Docker | `docker --version` |
| Puertos libres | `ss -ltn \| grep -E ':(7777\|7878)\b'` sin resultados |
| Espacio | `df -h /home`; la imagen ocupa ~104 MB comprimidos |
| `openssl` | `openssl version` |
| Binario compilado | `dotnet build -c Release` en el repo |
| OpenCode v2 | `opencode --version` |

Si el disco está justo, `docker system df` muestra lo reclamable. No borres caché sin revisar antes
qué se va a eliminar.

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

Prueba también un token incorrecto:

```bash
curl -s -o /dev/null -w '%{http_code}\n' \
  "http://127.0.0.1:7878/v2/server/status?token=incorrecto"
```

Debe devolver `403`.

---

## 4. Envoltorio para lanzar el MCP sin secretos en la configuración

OpenCode lanza el servidor MCP como proceso hijo. Para no escribir el token en la configuración del
proyecto, usa un pequeño envoltorio que lo lee del archivo con permisos `600`.

Crea `scripts/run-tshock-mcp.sh` en el repositorio:

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
cd /home/luisma/dev/hobbies/tshock-mcp

opencode mcp add tshock \
  --env TSHOCK_REST_URL=http://127.0.0.1:7878 \
  --env TSHOCK_MCP_ALLOW_WRITE=false \
  -- /home/luisma/dev/hobbies/tshock-mcp/scripts/run-tshock-mcp.sh
```

Comprueba qué archivo escribió:

```bash
git status --short
opencode mcp list
```

Debe aparecer `✓ tshock connected`. El CLI de OpenCode v2 escribe en `mcp.servers`. Si el comando
creó una entrada en la configuración global, bórrala y repite desde la raíz del proyecto.

Configuración manual equivalente, si prefieres editar el archivo:

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "tshock": {
        "type": "local",
        "command": [
          "/home/luisma/dev/hobbies/tshock-mcp/scripts/run-tshock-mcp.sh"
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

Si OpenCode ya estaba abierto:

```bash
opencode service restart
opencode mcp list
```

Las tools aparecen como `tshock_server_status`, `tshock_players_list`, etc. Con Code Mode se agrupan
bajo `tools.tshock.*`.

---

## 6. Batería de pruebas: solo lectura

Pide al agente, en este orden:

| Paso | Petición al agente | Resultado esperado |
| --- | --- | --- |
| 1 | «Usa `tshock_server_status` y dime el estado» | Nombre, versiones, jugadores, uptime |
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
Puedes probarlo reiniciando el MCP con otra variable de entorno o directamente con `curl`.

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
| `save_world` | «Guarda el mundo» | Archivo `.wld` actualizado |
| `reload_config` | «Recarga la configuración» | Respuesta de éxito |
| `kick_player` | Con un jugador conectado: «Expulsa a X» | El jugador pierde la conexión |

`kill_player` y `mute_player` requieren un jugador conectado. Para probar la redacción, conecta un
cliente de Terraria y pide `player_info`: la IP debe aparecer como `<redacted>`.

Al terminar, vuelve a `TSHOCK_MCP_ALLOW_WRITE=false` y reinicia el servicio.

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
| `403` en todas las rutas | Token incorrecto o grupo sin permisos | Revisar `-rest-token` y el grupo asociado |
| El contenedor no puede escribir en los volúmenes | Propietario del directorio | Ajustar propietario o ejecutar con `--user` |
| `tshock` no aparece en `opencode mcp list` | Configuración en el proyecto equivocado o servicio sin reiniciar | Repetir desde la raíz, `opencode service restart` |
| El host rechaza la sesión MCP | El comando escribe en stdout o la ruta no existe | Usar el binario compilado y el envoltorio; revisar logs |
| `write_disabled` | Escritura desactivada | `TSHOCK_MCP_ALLOW_WRITE=true` y reiniciar |
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

- Regiones y eventos (Fase 1).
- Descubrimiento y ejecución de comandos (Fase 1).
- Transporte HTTP y OAuth (Fase 2).

El plan de las fases siguientes está en [`roadmap.md`](roadmap.md).
