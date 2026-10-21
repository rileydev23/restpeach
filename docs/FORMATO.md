# Formato de la colección

Todo lo que RestPeach guarda de una colección es texto plano dentro de la carpeta `.api/` del proyecto
(también se reconoce `api-client/`). No hay nada más: si un archivo no está aquí, RestPeach no lo usa.

## Reglas comunes

- **JSON determinista:** claves ordenadas (ordinal), sangría de dos espacios, LF, UTF-8 sin BOM, salto de línea
  final. Guardar una petición sin cambiarla no produce diff.
- **Sin valores por defecto:** lo que vale lo de siempre (auth heredada, sin headers, body vacío…) no se
  escribe, así los archivos quedan cortos.
- **Los campos desconocidos se conservan.** Lo que agregues a mano y RestPeach no entienda se vuelve a escribir
  tal cual.
- **El archivo se llama como la petición** (`name: "Get post"` → `Get post.req.json`, sin los caracteres que
  Windows no acepta en un nombre de archivo). Si un archivo escrito a mano no tiene `name`, se usa el nombre
  del archivo.
- Las peticiones pueden ser `.req.json` o `.req.yaml` (también `.req.yml`); se guardan en el formato en el que
  están.

## `config.json` (la colección)

```json
{
  "auth": { "type": "bearer", "token": "{{TOKEN}}" },
  "defaultEnvironment": "local",
  "followRedirects": true,
  "name": "shop-api",
  "timeoutMs": 30000,
  "variables": { "API_VERSION": "v2" },
  "verifyTls": true
}
```

| Campo | Qué es |
|---|---|
| `name` | Nombre de la colección. |
| `defaultEnvironment` | Entorno elegido la primera vez (el nombre del archivo sin `.env.json`). |
| `auth` | Auth que heredan las carpetas y peticiones que dicen "heredar". Ver [Auth](#auth). |
| `variables` | Variables de toda la colección (la capa más baja). |
| `timeoutMs` | Tiempo máximo de cada petición (100 ms a 1 h). |
| `followRedirects` | Seguir redirecciones 3xx. |
| `verifyTls` | Verificar el certificado del servidor. Desactívalo solo para servidores locales. |
| `order` | Orden de lo que está en la raíz de la colección (ver [Orden](#orden)). |

## `folder.json` (una carpeta, opcional)

```json
{
  "auth": { "type": "bearer", "token": "{{TOKEN}}" },
  "variables": { "RESOURCE": "posts" }
}
```

Vale para todo lo que está debajo de la carpeta. Si no define nada, el archivo no existe.

### Orden

```json
{ "order": [ "Login.req.json", "Me.req.json", "Admin" ] }
```

`order` (en `folder.json`, o en `config.json` para la raíz) lista nombres de archivos y carpetas en el orden en que
se muestran y se ejecutan. Al importar se guarda el orden de la colección original (el login antes que lo que usa su
token). Lo que no está en la lista va después: carpetas primero y luego peticiones, en orden natural (`2.` antes que
`10.`). Renombrar o mover desde la app mantiene la lista al día.

## Peticiones (`<nombre>.req.json`)

```json
{
  "auth": { "type": "inherit" },
  "body": {
    "data": "{\n  \"title\": \"Peach\"\n}",
    "type": "json"
  },
  "description": "Crea un post. Devuelve 201.",
  "disabledParams": [ { "active": false, "key": "debug", "value": "1" } ],
  "headers": [ { "active": true, "key": "Accept", "value": "application/json" } ],
  "method": "POST",
  "name": "Create post",
  "tests": [ "status == 201", "set POST_ID = $.id" ],
  "url": "{{BASE_URL}}/posts?lang=es"
}
```

| Campo | Qué es |
|---|---|
| `method` | `GET`, `POST`, `PUT`, `PATCH`, `DELETE`, `OPTIONS` o `HEAD`; `WS` para un WebSocket (ver [WebSockets](#websockets)). |
| `url` | La URL con sus parámetros **activos** en la query. |
| `disabledParams` | Parámetros desmarcados en la tabla Params: se guardan pero no se envían. |
| `headers` | Lista de `{ active, key, value }`. `Content-Type` se agrega según el body si no lo pones; `Authorization` sale de la auth. |
| `auth` | Ver [Auth](#auth). Por defecto `inherit`. |
| `body` | Ver [Body](#body). |
| `tests` | Pruebas y capturas, una por elemento. Ver el [README](../README.md#pruebas-declarativas). |
| `description` | Notas libres (pestaña Notas). |
| `messages` | Solo WebSocket: mensajes guardados, en orden. Ver [WebSockets](#websockets). |
| `followRedirects` | Solo para esta petición: `false` no sigue redirecciones aunque la colección sí. Si no está, vale el de la colección. |

En YAML es lo mismo:

```yaml
method: GET
name: Get user
url: "{{BASE_URL}}/users/{{USER_ID}}"
tests:
  - status == 200
  - $.email exists
```

### Auth

| `type` | Campos | Se envía como |
|---|---|---|
| `inherit` | | La auth de la carpeta más cercana que tenga una, o la de la colección. |
| `none` | | Nada. |
| `bearer` | `token` | `Authorization: Bearer <token>` |
| `basic` | `username`, `password` | `Authorization: Basic <base64>` |
| `apikey` | `key`, `value`, `in` (`header` o `query`) | Un header `<key>: <value>` o el parámetro `?<key>=<value>`. |

Si la petición ya tiene un header `Authorization`, la auth no lo reemplaza.

### Body

| `type` | Campos | Content-Type por defecto |
|---|---|---|
| `none` | | |
| `json`, `text`, `xml`, `html` | `data` (texto) | `application/json`, `text/plain`, `application/xml`, `text/html` |
| `urlencoded` | `fields` | `application/x-www-form-urlencoded` |
| `form-data` | `fields`; un campo con `"type": "file"` envía el archivo de `value` (ruta relativa al proyecto) | `multipart/form-data` con su *boundary* |
| `graphql` | `query` (texto) y `variables` (objeto JSON, o texto si tiene `{{VARIABLES}}` fuera de comillas) | `application/json` |

Un body `graphql` se envía como `{"query": …, "variables": …}` en JSON; con `GET`, como `?query=…&variables=…` en la
URL. En el árbol la petición se marca **GQL**; las que mandan JSON-RPC 2.0 (`"jsonrpc": "2.0"`) se marcan **RPC** y
las que mandan un sobre SOAP, **SOAP**. Al recibir la respuesta, una línea sobre ella resume lo que significa en ese
protocolo (errores de GraphQL o JSON-RPC que llegan con 200, `soap:Fault`, eventos SSE, líneas NDJSON, errores
RFC 7807).

Solo se escriben los campos que usa el tipo, así cambiar de tipo en la app no deja restos en git.

### WebSockets

```json
{
  "body": { "data": "{\"type\":\"ping\"}", "type": "json" },
  "messages": [
    { "data": "{\"type\":\"join\",\"room\":\"dev\"}", "name": "join" },
    { "data": "{\"type\":\"who\"}", "name": "who" }
  ],
  "method": "WS",
  "name": "Chat",
  "tests": [ "status == 101", "$[0].type == \"welcome\"" ],
  "url": "{{BASE_URL}}/ws/chat?token={{TOKEN}}"
}
```

Con `method: "WS"` (o una `url` que empieza con `ws://` o `wss://`) la petición es un WebSocket, marcado **WS** en
el árbol. Una URL `http://` o `https://` se convierte en `ws://` o `wss://`, así se comparte `{{BASE_URL}}` con las
peticiones REST. `headers`, `auth` (Bearer, Basic o API key) y las cookies de la sesión van en el *handshake*;
`Sec-WebSocket-Protocol` pide subprotocolos (separados por comas).

- `body` es el mensaje que se está escribiendo: `json` o `text`, sin Content-Type. Sus `{{variables}}` se reemplazan
  al enviarlo.
- `messages` son los mensajes guardados (`name` y `data`), en el orden en que se guardaron.
- Al **ejecutar** la colección (o con `restpeach-cli run`) se conecta, se envía `body` si tiene algo, se junta lo que
  llega hasta que el servidor calla un segundo (o cierra, o vence el timeout) y se cierra. La respuesta tiene status
  101 y los headers del *handshake*, y su body es un array JSON con los mensajes recibidos (cada uno como JSON si lo
  es, si no como texto), así que valen las mismas pruebas: `status == 101`, `$[1].type == "pong"`,
  `body contains "hola"`. Sin pruebas, falla si el servidor rechaza el *upgrade* o cierra con un código de error
  (todo lo que no sea 1000, 1001 o 1005).
- Exportar a Postman, OpenAPI o bash deja fuera los WebSockets: esos formatos no tienen cómo describirlos.

## Entornos

`environments/<nombre>.env.json` guarda las variables **compartidas** (van al repositorio):

```json
{
  "name": "Local",
  "variables": {
    "BASE_URL": "http://localhost:3000",
    "USER_ID": "1"
  }
}
```

`environments/.env.secrets.json` guarda los **secretos** de todos los entornos, por nombre de archivo. El
`.gitignore` de la colección lo deja fuera de git, así que cada persona tiene el suyo:

```json
{
  "local": { "TOKEN": "eyJhbGciOi…" },
  "staging": { "TOKEN": "…" }
}
```

Un secreto gana a una variable compartida con el mismo nombre. En CI, los secretos se pasan con
`restpeach-cli run --var TOKEN=…`.

## Variables

`{{NOMBRE}}` se reemplaza en la URL, los headers, el body, los campos de formulario y la auth. Orden de
prioridad, de menor a mayor: colección < carpetas (de afuera hacia adentro) < entorno < secretos < valores
capturados en la sesión (`set`, o `--var` en el CLI). Un valor puede usar otras variables. Un nombre que no
existe se deja tal como se escribió y se avisa.

Variables dinámicas: `{{$guid}}` (también `$uuid`, `$randomUUID`), `{{$timestamp}}` (segundos Unix),
`{{$isoTimestamp}}` (UTC, ISO 8601) y `{{$randomInt}}` (0 a 1000).

## `.gitignore` y `.gitattributes`

RestPeach crea los dos dentro de `.api/` (nunca toca los de la raíz del proyecto):

```gitignore
# Secrets stay on this machine (see environments/*.env.json for the shared values)
environments/.env.secrets.json
```

```gitattributes
# RestPeach writes these files with LF and sorted keys; keep them byte-for-byte in git.
* text=auto eol=lf
```
