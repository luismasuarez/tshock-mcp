# Seguridad

Este documento explica cómo reportar una vulnerabilidad, qué versiones reciben correcciones y qué
cubre el proyecto. El modelo de amenazas completo y las recomendaciones para operadores están en
[docs/security.md](docs/security.md); este archivo es el punto de entrada para quien encuentra un
problema de seguridad en el gateway.

## Versiones soportadas

El proyecto está antes de la versión 1.0. La API, el conjunto de herramientas y el sobre de salida
pueden cambiar entre versiones menores sin un ciclo formal de deprecación. Fije una versión concreta
si necesita estabilidad y revise las notas de cada publicación antes de actualizar.

| Versión | Soporte |
| --- | --- |
| `0.1.x` | Recibe correcciones de seguridad y de errores. |
| Anterior a `0.1.0` | Sin soporte. Actualice a la última `0.1.x`. |

Las correcciones se aplican sobre la rama principal y se publican en la serie `0.1.x` vigente. No hay
ramas de mantenimiento para versiones antiguas.

## Cómo reportar una vulnerabilidad

Use el formulario de aviso privado de seguridad de GitHub:

<https://github.com/luismasuarez/tshock-mcp/security/advisories/new>

**No abra un issue público para un problema de seguridad**, ni siquiera para solicitar un canal
privado. El repositorio es público y un issue expone la debilidad antes de que exista una corrección,
además de dejar el detalle indexado de forma permanente.

Incluya en el reporte:

- La versión afectada, tomada de la etiqueta, el paquete o el hash del commit.
- La versión de .NET y el sistema operativo, si son relevantes.
- El detalle de la reproducción: herramienta MCP invocada, argumentos, anfitrión MCP y versión de
  TShock.
- El comportamiento observado y el esperado.
- Una evaluación del impacto: qué puede leer, modificar o interrumpir un atacante.
- La configuración involucrada, solo con nombres de variables y valores no secretos.

Nunca incluya el token de TShock, credenciales reales ni direcciones IP de jugadores en el reporte.
Si necesita mostrar una respuesta, redacte tales valores primero.

El proyecto es comunitario y se mantiene de forma voluntaria: no hay un plazo de respuesta
garantizado. El objetivo es confirmar la recepción, evaluar el impacto y coordinarse con quien
reporta antes de publicar un aviso. Si el problema afecta a TShock y no al gateway, corresponde
reportarlo al proyecto TShock.

## Modelo de amenazas en resumen

El gateway es un proceso local que habla MCP por stdio y consume la API REST de TShock con un token
de aplicación. El modelo nunca ve el token ni se comunica directamente con TShock; el operador
controla la configuración, el token y el permiso de escritura. El detalle completo, con activos,
actores y límites de confianza, está en [docs/security.md](docs/security.md).

**El token.** `TSHOCK_TOKEN` se lee solo del entorno y nunca se escribe en logs, mensajes de error,
trazas, el registro de auditoría ni la salida de las herramientas. Los campos `token` y `password` se
redactan siempre. TShock exige el token como parámetro de consulta, de modo que puede quedar en sus
propios registros, en un proxy inverso o en herramientas de diagnóstico de red; el gateway no puede
controlar tales destinos. Un token filtrado debe rotarse de inmediato.

**La escritura está cerrada por defecto.** Las 6 herramientas de escritura comprueban
`TSHOCK_MCP_ALLOW_WRITE` en cada llamada, no al arrancar. Con el valor por defecto (`false`)
devuelven `write_disabled` sin ninguna petición HTTP. Aun con la escritura habilitada, TShock sigue
siendo la autoridad de permisos: el filtro local es una segunda barrera, no un reemplazo.

**Las IP se redactan por defecto.** Los campos `ip` y los valores con forma de IP en campos de
identidad se reemplazan por `<redacted>` salvo que se defina `TSHOCK_MCP_SHOW_IPS=true`. La redacción
es una medida de privacidad frente al modelo y sus registros, no un control de acceso.

**El canal de datos está separado del diagnóstico.** stdout transporta exclusivamente mensajes
JSON-RPC; todos los logs, advertencias y trazas van a stderr. La configuración es solo por variables
de entorno, se valida al arrancar y no admite archivos ni argumentos de línea de comandos. El valor
por defecto de `TSHOCK_REST_URL` usa loopback y el HTTP sin cifrar hacia un host remoto se rechaza
salvo que se active `TSHOCK_MCP_ALLOW_INSECURE=true` de forma explícita.

**La salida no es confiable.** Nombres de jugadores, chat, MOTD, reglas y nombres de mundo pueden
ser escritos por terceros. Un jugador puede intentar inyectar instrucciones en ese contenido. El
modelo debe tratar la salida de las herramientas como datos, nunca como órdenes, y el operador debe
reducir el poder del agente y supervisar su uso cuando la escritura está habilitada.

## Redacción de IP y registros de TShock

La redacción del gateway cubre únicamente lo que devuelven las herramientas MCP. **No cubre los logs
de TShock.** Al entrar un jugador, el servidor de juego escribe su dirección real, por ejemplo
`Luisma has joined. IP: 192.0.2.10`, en la salida del proceso, en `docker logs` o en
`$DATA/tshock/logs`. Tales archivos crecen fuera del control del gateway.

Trate los logs de TShock como datos sensibles: límite quién puede leerlos, no los publique en issues,
capturas ni reportes y aplique la retención que corresponda. La redacción de la salida de las
herramientas no corrige ni filtra ese canal.

## Alcance

**Dentro del alcance:** el código del gateway en `src/TShock.MCP.Server`: cliente REST, redacción de
respuestas, sobre de resultados, validación de configuración, registro de auditoría, definición de las
18 herramientas, el `Dockerfile` y los scripts del repositorio. También entra cualquier fallo que
permita saltarse un invariante documentado en [AGENTS.md](AGENTS.md), como registrar el token, mutar
estado con la escritura deshabilitada o mezclar diagnóstico con stdout.

**Fuera del alcance:**

- TShock, su API REST y su modelo de permisos. Un fallo en TShock corresponde a su proyecto.
- Terraria y el cliente de juego, propiedades de Re-Logic.
- La configuración del host: firewall, sistema operativo, propiedad y modo de los archivos,
  terminación de TLS, redes de Docker y el aislamiento del proceso.
- El nivel de privilegio del token que crea el operador. Un token demasiado poderoso es un error de
  configuración, no una vulnerabilidad del gateway.
- El comportamiento del anfitrión MCP o del modelo, incluida la obediencia a instrucciones
  incrustadas en la salida de las herramientas.
- Servicios de terceros y la infraestructura donde se despliega el proyecto.

Un reporte sobre un elemento fuera del alcance se responde con una explicación y, si aplica, se
redirige al proyecto correspondiente.

## Lista de verificación del operador

Antes de exponer el gateway a un modelo, y sobre todo antes de habilitar la escritura:

1. Lea [docs/security.md](docs/security.md) y su lista de verificación de 8 puntos.
2. Use un token dedicado, con un grupo acotado al mínimo necesario y sin reutilizarlo entre
   servidores.
3. Mantenga `TSHOCK_MCP_ALLOW_WRITE=false` salvo que exista una necesidad concreta y supervisión.
4. Conecte por loopback o por HTTPS con un certificado válido; no use HTTP remoto sin cifrar.
5. Guarde el registro de auditoría fuera del repositorio y con acceso restringido.
6. Restrinja el acceso a los logs de TShock, que contienen IP reales.
7. Indique al modelo, en el prompt del sistema, que la salida de las herramientas es datos no
   confiables.
8. Rote el token de inmediato si sospecha que se filtró.

La guía completa, con el procedimiento de creación del token y la política de operaciones
destructivas, está en [docs/security.md](docs/security.md).
