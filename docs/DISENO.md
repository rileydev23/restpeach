# Sistema de diseño "Peach"

Reglas visuales y de interacción compartidas por las apps de escritorio de la familia **Peach**
(GitPeach, TextPeach, RestPeach y las que vengan). Están extraídas del código real de GitPeach y aplicadas en
TextPeach y RestPeach.
El objetivo de este documento es que una app nueva se vea y se comporte igual sin tener que abrir las
anteriores: si algo no está aquí, copia lo que hace GitPeach y agrégalo a este documento.

> **Regla de oro:** una app Peach parece un IDE sobrio. Fondo gris carbón, texto gris claro, un solo azul
> de acento, y color solo cuando significa algo (estado, tipo, advertencia). Nada decorativo.

Índice:

1. [Principios](#1-principios)
2. [Base técnica](#2-base-técnica)
3. [Color](#3-color)
4. [Tipografía](#4-tipografía)
5. [Espaciado, forma y bordes](#5-espaciado-forma-y-bordes)
6. [Anatomía de la ventana](#6-anatomía-de-la-ventana)
7. [Componentes](#7-componentes)
8. [Patrones de interacción](#8-patrones-de-interacción)
9. [Teclado](#9-teclado)
10. [Textos y tono](#10-textos-y-tono)
11. [Iconografía y marca](#11-iconografía-y-marca)
12. [Datos, métricas y gráficos](#12-datos-métricas-y-gráficos)
13. [Rendimiento percibido](#13-rendimiento-percibido)
14. [Plantillas de código](#14-plantillas-de-código)
15. [Checklist para una app nueva](#15-checklist-para-una-app-nueva)
16. [Anexo: dónde se aplica cada regla](#16-anexo-dónde-se-aplica-cada-regla)
17. [Anexo RestPeach: colores y patrones propios](#17-anexo-restpeach-colores-y-patrones-propios)

---

## 1. Principios

1. **Oscuro y denso, como un IDE.** Solo tema oscuro (`RequestedThemeVariant="Dark"`). Filas compactas,
   fuente base de 13 px, mucha información visible a la vez. No hay modo claro.
2. **El color es información.** El 90 % de la pantalla son grises. El azul de acento marca lo seleccionado y la
   acción principal. Verde, rojo, ámbar, morado… solo aparecen para comunicar estado o tipo, siempre con el
   mismo significado en todas las apps (ver [3.4](#34-colores-semánticos)).
3. **Una acción principal por zona.** En cada barra hay como máximo un botón `accent` (azul). El resto son
   botones neutros.
4. **Lo destructivo nunca es por defecto.** Borrar, descartar, sobrescribir o editar requieren un gesto
   explícito y, si no se puede deshacer, una confirmación. "Quitar de la lista" dice que no borra nada del disco.
   En TextPeach los archivos se abren en **solo lectura**; editar es un botón ámbar aparte.
5. **Nada se mueve solo.** La ventana se abre donde la dejaste, invisible hasta estar colocada. Las listas se
   reemplazan de una vez (no fila por fila). Al recargar un archivo se conserva el cursor y el scroll.
6. **Todo se puede hacer con teclado.** Cada acción frecuente tiene atajo, los atajos se listan en F1 y siguen
   las mismas convenciones en todas las apps ([9](#9-teclado)).
7. **Estado persistente.** Pestañas abiertas, pestaña activa, tamaño/posición de la ventana, favoritos y
   preferencias se guardan solos en `state.json`.
8. **Español en la interfaz, inglés en el código.** Textos visibles en español neutro tuteando; nombres de
   clases, variables y comentarios en inglés.
9. **Sin nombres de terceros.** La interfaz nunca menciona ni lanza aplicaciones de terceros (editores, IDEs, servicios). Lo permitido: **"Abrir con la aplicación predeterminada"** (delega en el sistema), las herramientas del propio sistema cuando la app las necesita para trabajar (GitPeach mantiene Explorador y Terminal) y las apps de la propia familia (GitPeach abre repositorios y archivos en TextPeach, con aviso si no está instalada).
10. **Lo que solo se lee nunca parece editable.** Texto informativo, mensajes, atajos y documentos se muestran con `TextBlock`/`SelectableTextBlock`: sin cursor parpadeante, sin fondo de campo, sin borde de foco. `TextBox` es exclusivamente para escribir.
11. **Portable y autocontenido.** Un único `.exe` sin instalador. Con un `portable.txt` al lado, los datos se
   guardan junto al exe.

---

## 2. Base técnica

| Pieza | Elección | Notas |
|---|---|---|
| UI | **Avalonia 11.3** + `Avalonia.Themes.Fluent` | `RequestedThemeVariant="Dark"`, estilos propios encima (`Styles.axaml`). |
| Runtime | **.NET 10** (`net10.0`), C# `latest`, `Nullable` y `ImplicitUsings` activos | `InvariantGlobalization=true`, `SatelliteResourceLanguages=en`. |
| MVVM | **CommunityToolkit.Mvvm 8.4** | `[ObservableProperty]` sobre campos `_camelCase`, `[RelayCommand]`. |
| Bindings | `AvaloniaUseCompiledBindingsByDefault=true` | Todo XAML declara `x:DataType`. |
| Render | Software (`Win32RenderingMode.Software`) | Se excluye `av_libglesv2.dll` al publicar (≈25 MB menos). |
| DPI | `app.manifest` con `PerMonitorV2` | |
| Publicación | `build.cmd` → single-file, self-contained, `win-x64`, ReadyToRun | `dev.cmd` compila Debug y ejecuta. |
| Estado | `%APPDATA%\<App>\state.json` o `.\data\state.json` si existe `portable.txt` | JSON con *source generator*, escritura atómica (`.tmp` + `Move`). |
| Tests | xUnit en `tests/<App>.Tests` | Lógica pura (parsers, analizadores) siempre testeada. |

Editor de código (cuando haga falta): **AvaloniaEdit 11.4** + **AvaloniaEdit.TextMate** con el tema
`peach-dark.json` ([3.8](#38-resaltado-de-sintaxis-peach-dark)). Si una app usa TextMate no se publica con
*trimming* (las gramáticas se cargan por reflexión).

---

## 3. Color

Todos los valores son hexadecimales sRGB. En XAML se escriben en minúsculas (`#1e1f22`). En código se usan
desde `Controls/Palette.cs`; nunca inventes un gris nuevo: elige el más cercano de esta tabla.

### 3.1 Superficies (de más oscura a más clara)

| Token (`Palette.*`) | Hex | Uso |
|---|---|---|
| `Chrome` | `#18191c` | Barra de pestañas de la ventana, barra de estado, log de comandos. Lo "más exterior". |
| `Code` | `#1b1c1f` | Fondo de código y diffs (editor, visor de imágenes). |
| `Bg` | `#1e1f22` | Fondo de ventana y de contenido general. Fondo de listas dentro de diálogos. |
| `Sidebar` | `#222327` | Paneles laterales (sidebar de ramas, árbol de archivos, panel de análisis), tarjetas del resumen. |
| `Panel` | `#26272b` | Barras de herramientas, cabeceras de sección/archivo, fondo de diálogos, `TextBox`, *stat tiles*. |
| `Panel2` | `#2d2e33` | Botón normal, *hover* de filas, chips. |
| `FileBg` | `#34363c` | Cabecera de archivo dentro de un diff. |
| `Border` | `#3a3c42` | **Todos** los bordes, separadores, `GridSplitter`, *hover* de botón, contador tipo *pill*. |

Reglas:

- Cada franja horizontal (pestañas, toolbar, banner, contenido, estado) cambia de superficie y se separa con
  un borde de 1 px `#3a3c42` en el lado que toca al contenido (`BorderThickness="0,0,0,1"` arriba,
  `"0,1,0,0"` abajo).
- Nunca uses blanco ni grises claros como fondo. El fondo más claro permitido es `#3a3c42` (y solo como borde
  o *hover*).
- Línea actual del editor: `#232428` (un paso sobre `Code`).

### 3.2 Texto

| Token | Hex | Uso |
|---|---|---|
| `Text` | `#d4d4d4` | Texto normal. |
| `ContextFg` | `#c8c8c8` | Líneas de contexto en diffs; número de línea activo. |
| `Dim` | `#8a8d93` | Texto secundario: rutas, fechas, metadatos, etiquetas, *watermarks*, botón ✕ de pestañas, cabeceras del sidebar. Clase `TextBlock.dim`. |
| (gutter) | `#5d6066` | Números de línea del editor. |
| (comentario) | `#6f737a` | Comentarios en código (cursiva). |
| Blanco | `#ffffff` | **Solo** sobre fondos de color: botón `accent`, badges de refs, pestaña/segmento seleccionado. |

La jerarquía se consigue con `Text` vs `Dim` y con peso (SemiBold), no con tamaños muy distintos.
Patrón de ruta: directorio en `Dim` + nombre de archivo en `Text` (o SemiBold), uno pegado al otro.

### 3.3 Acento y selección

| Uso | Hex |
|---|---|
| Acento (botón principal, cursor, subrayado de segmento activo, rama actual) | `#4a90d9` |
| Acento *hover* | `#5aa0e9` |
| Fila/pestaña seleccionada (`ListBoxItem:selected`, `TreeViewItem:selected`, `ToggleButton:checked`, selección de texto) | `#33507a` |
| Selección + *hover* | `#3a5a88` |
| Fila en *hover* (no seleccionada) | `#2d2e33` |

### 3.4 Colores semánticos

El significado es fijo en toda la familia. Si un color ya tiene significado, no lo reutilices para otra cosa.

| Nombre | Hex | Significado |
|---|---|---|
| Verde | `#6cc070` | Positivo / identidad: rama actual, tipo de proyecto, contador de archivos, archivo añadido (`A`, `?`). |
| Rojo | `#e0645a` | Error, eliminado (`D`), "carpeta no encontrada", `FIXME`/`BUG`. |
| Ámbar | `#e0a24a` | Atención: favorito ★, ahead/behind, conflicto (`U`), valor de métrica fuera de rango, `HACK`/`XXX`, carpetas en el árbol, clases en el outline, "Editando". |
| Ámbar claro (texto sobre banner) | `#f0c890` | Texto de aviso sobre `#5a4318`. |
| Azul | `#4a90d9` | Modificado (`M`), `TODO`, encabezados, claves. |
| Morado | `#9a7ae0` | Renombrado/copiado (`R`, `C`), funciones y métodos. |
| Teal | `#4ac7c0` | Interfaces, structs, tipos. |
| Rosa | `#d96a9b` | Selectores CSS; palabras clave en código. |
| Oliva | `#c8c84a` | Enums; decoradores/atributos en código. |

Letras de estado de archivo (`A`, `M`, `D`, `R`, `U`…): **Bold**, del color semántico, columna fija de 22 px.

### 3.5 Banners (franjas de aviso)

Ocupan todo el ancho, debajo de la toolbar, `Padding="10,5"`/`"10,6"`, texto SemiBold, botones a la derecha.

| Tipo | Fondo | Cuándo |
|---|---|---|
| Atención / operación en curso | `#5a4318` (borde de botón `#8a6d1f`) | Merge/rebase en curso (GitPeach), **modo edición** (TextPeach). |
| Información | `#25364d` | "El archivo cambió en el disco", cabecera de *hunk*. |

### 3.6 Diffs

| Elemento | Fondo | Texto |
|---|---|---|
| Línea añadida | `#1d3a2a` | `#a6e3b4` |
| Línea eliminada | `#431f22` | `#f4a6a6` |
| Contexto | transparente | `#c8c8c8` |
| Cabecera de hunk | `#25364d` | `Dim` |
| Cabecera de archivo | `#34363c` | `Text`, SemiBold |
| Notas ("Archivo binario…") | — | `Dim`, cursiva |

Coincidencias de búsqueda (resultados de "Buscar en proyecto"): fondo `#5a4318`, texto `#ffd27a`.

### 3.7 Series categóricas (grafo, lenguajes, gráficos)

Orden fijo; se asigna por índice (`i % 8`):

| # | Hex | # | Hex |
|---|---|---|---|
| 0 | `#4a90d9` azul | 4 | `#9a7ae0` morado |
| 1 | `#e0a24a` ámbar | 5 | `#4ac7c0` teal |
| 2 | `#6cc070` verde | 6 | `#e0645a` rojo |
| 3 | `#d96a9b` rosa | 7 | `#c8c84a` oliva |

Badges de refs (GitPeach), texto blanco 11 px, `CornerRadius=3`, `Padding="5,1"`:
local `#2f6f3e`, remota `#2d5c8a`, tag `#8a6d1f`, HEAD `#7a3b8a`, rama actual `#4a90d9`.

Color por lenguaje (TextPeach, `Services/Languages.cs`): cada lenguaje usa uno de los 8 colores de la serie
(p. ej. C# morado, TypeScript azul, JavaScript oliva, Python azul, Rust ámbar, HTML rojo, JSON oliva);
texto plano y desconocidos usan `Dim`. Se muestran como un punto `●` de 9–10 px antes del nombre.

### 3.8 Resaltado de sintaxis "Peach Dark"

Archivo: `Assets/peach-dark.json` (formato de tema TextMate). Derivado de la serie:

| Ámbito TextMate | Hex | Estilo |
|---|---|---|
| Texto por defecto | `#d4d4d4` sobre `#1b1c1f` | |
| `comment` | `#6f737a` | cursiva |
| Comentario de documentación | `#7c8a7f` | cursiva |
| `keyword`, `storage.type`, `storage.modifier` | `#d96a9b` | |
| `keyword.operator` | `#a9acb2` | |
| `punctuation` | `#8a8d93` | |
| `string` | `#8fcf94` | |
| Escapes, regex, *placeholders* | `#e0645a` | |
| `constant.numeric`, `constant.language` | `#e0a24a` | |
| Tipos, clases, namespaces (`entity.name.type`, `support.class`) | `#4ac7c0` | |
| Funciones (`entity.name.function`) | `#7db4ea` | |
| Propiedades / claves (`variable.other.property`, `support.type.property-name`) | `#a8c8e8` | |
| `this`/`self` | `#d96a9b` | cursiva |
| Etiquetas HTML/XML | `#4a90d9` | |
| Atributos | `#9a7ae0` | |
| Decoradores / atributos C# | `#c8c84a` | |
| Preprocesador | `#9a7ae0` | |
| Markdown: títulos `#4a90d9` bold · código `#e0a24a` · citas `Dim` cursiva · enlaces `#7db4ea` | | |
| Diff dentro de código: añadido `#a6e3b4`, eliminado `#f4a6a6`, cambiado `#e0a24a` | | |
| `invalid` | `#e0645a` | |

Editor: selección `#33507a`, línea actual `#232428`, cursor `#4a90d9`, *gutter* `#5d6066`,
marcadores de plegado `Dim` sobre `Code` (activo: `Text` sobre `#2d2e33`).

---

## 4. Tipografía

**Familia UI:** `Segoe UI` (en `Window`). **Monoespaciada:** `Cascadia Mono, Consolas, Courier New`
(clase `TextBlock.mono`; el editor usa la misma pila).

| Tamaño | Uso |
|---|---|
| 9–10 | Glifos `●`/`■` de tipo, ✕ de pestañas internas, números de línea en listas laterales, etiquetas `TODO`. |
| 11 | Metadatos bajo un título (ruta en Quick Open), badges, chips, etiquetas de *stat tile*, botones pequeños dentro de cabeceras (`Padding="8,1"`). |
| 12 | Listas densas (sidebar, árbol, archivos, outline, métricas), código y diffs, rutas bajo el nombre. |
| **13** | **Base** de toda la interfaz (botones, pestañas, texto). |
| 14 | Nombre principal de una fila grande (proyecto/repo en la Home), mensaje del commit seleccionado. |
| 16 | Título de un *empty state*. |
| 18 | Título de una vista de resumen. |
| 20 | Valor numérico de un *stat tile*. |

Pesos: `Normal` por defecto; **SemiBold** para títulos de sección, nombres principales, elemento actual
(rama actual, WIP, pestaña activa del segmento, primer nivel del outline); **Bold** solo para letras de
estado y etiquetas de tipo. Cursiva solo para notas ("Cargando diff…", "Sin resultados") y comentarios.

Recorte: listas y cabeceras usan `TextTrimming="CharacterEllipsis"`; rutas largas
`LeadingCharacterEllipsis` (se conserva el nombre del archivo).

---

## 5. Espaciado, forma y bordes

Unidad base de 2 px; los valores habituales son 2, 4, 6, 8, 10, 12, 14, 18.

| Elemento | Medida |
|---|---|
| Toolbar | `Padding="8,6"` (vista de trabajo) o `"10"` (Home). Botones con `Spacing="4"`–`"6"`. |
| Separador vertical en toolbar | `Border Width="1" Background="#3a3c42" Margin="6,2"`. |
| Barra de estado | `Padding="10,3"`–`"10,4"`. |
| Fila grande (Home) | `Margin="10,8"`, columnas `28,*,Auto`. |
| Fila densa (archivos, sidebar) | `Margin="8,2"`–`"8,3"`; sangría de árbol `+12` px por nivel. |
| Cabecera de sección dentro de un panel | `Margin="8,6"` / `Padding="10,6"`, título SemiBold, acción `flat` a la derecha. |
| Diálogo | `Margin="18"`, `Spacing="16"` entre mensaje y botones; formularios `Spacing="8"`; botones `MinWidth=84`, `Padding="12,5"`, a la derecha, el principal primero. |
| Tarjeta / *stat tile* | `CornerRadius=4`, `Padding="14,10"`, borde 1 px, separación 10 px. |
| Botón | `CornerRadius=3`, `Padding="10,4"`, borde 1 px. |
| Botón `flat` | sin borde, `Padding="6,2"`. |
| Chip (`ToggleButton.chip`) | `Padding="6,1"`, 11 px, `MinWidth=26`. |
| Badge | `CornerRadius=3`, `Padding="5,1"`. Contador tipo *pill*: `CornerRadius=7`, `Padding="6,0"`, fondo `#3a3c42`, 10 px. |
| `GridSplitter` | 4 px, color `#3a3c42`. |
| Pestaña de ventana | `Margin="12,7,6,7"`, `Spacing="8"`. |
| Pestaña de documento | `Margin="10,5,4,5"`, 12 px, separador derecho de 1 px. |
| Barra de progreso | indeterminada, alto 4–6 px, ancho 50–120 px, a la derecha de la barra de estado. |

Esquinas: 3 px controles, 4 px tarjetas, 0 en segmentos y pestañas. No hay sombras ni degradados en la UI
(los degradados son exclusivos del logo).

---

## 6. Anatomía de la ventana

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  🍑 TextPeach  proyecto · C:
uta                               ─   ☐   ✕   │  ← título    #111214 (34 px)
├──────────────────────────────────────────────────────────────────────────────┤
│▌Proyectos▐│ proyecto-a ✕ │ proyecto-b ✕ │                              │  ? │  ← pestañas  #18191c
├──────────────────────────────────────────────────────────────────────────────┤
│ [Acción] [Acción] │ [Acción]   info verde               [Externo] [Externo] │  ← toolbar   #26272b
├──────────────────────────────────────────────────────────────────────────────┤
│ Banner de aviso (solo si aplica)                          [Continuar] [Abortar]│  ← banner    #5a4318
├──────────────┬┬──────────────────────────────────────┬┬──────────────────────┤
│ Sidebar      ││ Contenido principal                  ││ Panel de detalle     │
│ #222327      ││ #1e1f22 / código #1b1c1f             ││ #222327              │
│ 200–260 px   ││ * (mín. 300–360)                     ││ 290–360 px           │
│              ││                                      ││                      │
├──────────────┴┴──────────────────────────────────────┴┴──────────────────────┤
│ aviso / estado (dim)                    Ln 1, Col 1 · UTF-8 · LF · C# · ▬▬▬  │  ← estado    #18191c
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Barra de título propia** (`Border.titlebar`, 34 px, `#111214`): la ventana no usa el marco de Windows
  (`ExtendClientAreaToDecorationsHint=True`, `ExtendClientAreaChromeHints=NoChrome`). A la izquierda, con 14 px de
  margen: icono 18 px, nombre de la app SemiBold y, en `dim` 12 px, el elemento abierto y su ruta
  ("textpeach · C:\…"). A la derecha, botones `Button.caption` de 46×34 dibujados con `Path` de 10 px y trazo
  1 px `#a8abb2` (minimizar, maximizar/restaurar, cerrar); *hover* `#26272b` y blanco; cerrar en *hover* rojo
  `#e0645a`. Arrastrar el espacio vacío mueve la ventana y el doble clic maximiza o restaura. El contenido usa
  `Margin="{Binding $parent[Window].OffScreenMargin}"` para no quedar fuera de pantalla al maximizar. El título
  de la ventana (barra de tareas, Alt+Tab) es "elemento — App".
- **Barra de pestañas** (`#18191c`, borde inferior, alto mínimo 36 px aunque no haya pestañas): **sin icono**
  (el icono solo va en la barra de título). A la izquierda, el botón fijo de la Home (`Button.home`: "Repositorios" / "Proyectos"),
  después las pestañas de elementos y, a la derecha, el botón `?` (F1, "Acerca de"). Pestaña seleccionada
  en `#33507a`. Tooltip de cada pestaña: la ruta completa.
- **Botón Home (`Button.home`)**: no es una pestaña más, es un lanzador de otro nivel. Fondo más oscuro
  `#111214`, texto `#a8abb2` SemiBold centrado (`Margin="18,0"` simétrico), esquinas rectas, alto completo
  de la barra y borde derecho 1 px `#3a3c42` que lo separa de las pestañas. Hover `#1f2024` con texto blanco;
  activo `#0d1520`, texto blanco y una línea inferior de 2 px `#4a90d9`. Las pestañas de elementos van en su
  propia lista (`RepoTabs` / `ProjectTabs`) a 4 px del botón.
- **Toolbar** (`#26272b`): acciones de la vista a la izquierda agrupadas con separadores verticales;
  a la derecha solo "Abrir con la app predeterminada" (deshabilitado si no hay un archivo); un dato de contexto en verde
  SemiBold al final de la izquierda (rama actual, nº de archivos).
- **Banner** opcional, solo cuando hay un estado que exige atención.
- **Cuerpo** en `Grid` de 3 columnas con `GridSplitter` de 4 px y `MinWidth` en cada columna.
  Las divisiones verticales internas usan filas con `GridSplitter` horizontal (alto mínimo 140).
- **Barra de estado** (`#18191c`, borde superior): a la izquierda el aviso o el texto de ocupado
  (`dim`, recortado); a la derecha datos de contexto (`dim`, separados por 14 px), toggles `flat` y
  progreso.

**Colocación de la ventana** (`App.PlaceWindow`, cópialo tal cual):
tamaño por defecto 1200×760 DIP sin superar el 92 % del área de trabajo; mínimo 800×500; recuerda tamaño,
posición, monitor y maximizado; siempre deja el marco completo dentro del área visible; la ventana arranca
con `Opacity=0` y se muestra al estar colocada.

**Ventanas secundarias** (diff aparte, blame, historial): mismo icono, título `"{nombre} — {Qué}"`,
`WindowStartupLocation="CenterOwner"`, nunca más del 95 % de la principal, cabecera `#26272b` con el
contexto.

---

## 7. Componentes

Todos los estilos base viven en `Styles.axaml` ([14.2](#142-stylesaxaml-base)). Usa clases, no colores
sueltos, salvo en fondos de franjas.

### 7.1 Botones

| Clase | Aspecto | Cuándo |
|---|---|---|
| (ninguna) | `#2d2e33`, borde `#3a3c42`, *hover* `#3a3c42` | Acciones normales. |
| `accent` | `#4a90d9`, texto blanco, sin borde, *hover* `#5aa0e9` | La acción principal de la zona (Agregar, Commit, Guardar, Aceptar). Máximo una por zona. |
| `flat` | transparente, sin borde | Acciones secundarias dentro de filas y cabeceras (✕, "Stage todo", "Limpiar", toggles de la barra de estado). |
| `warn` | `#5a4318`, borde `#8a6d1f` | Acciones que cambian de modo hacia algo riesgoso ("✎ Editar"). |
| `seg` (+ `active`) | transparente, subrayado inferior 2 px; activo: subrayado `#4a90d9`, texto blanco SemiBold | Conmutador de vistas dentro de un panel (Archivos / Buscar). |
| `ToggleButton` | como botón; marcado `#33507a` | Filtros persistentes (★ Favoritos). |
| `ToggleButton.chip` | chip pequeño 11 px | Opciones de búsqueda (`Aa`, `ab|`, `.*`). |

**Cursor:** mano (`Hand`) en todo lo que se pulsa como botón o enlace (`Button`, `ToggleButton`, `SplitButton`,
enlaces de documentos); flecha en filas de listas, árbol, pestañas, botones de ventana y botones deshabilitados.

Etiquetas: verbo o sustantivo corto. Si abre un diálogo, termina en `...` ("Agregar proyecto...").
Los botones de una sola letra o glifo (`✕`, `+`, `−`, `↶`, `⋯`) siempre llevan `ToolTip.Tip`.

### 7.2 Campos de texto

`TextBox` con fondo `#26272b` y borde `#3a3c42`; *watermark* en español terminado en `…`/`...`
("Filtrar proyectos...", "Buscar en todos los archivos…"). El filtro principal de una vista recibe el foco al
abrirla. `Esc` limpia; `Enter` ejecuta; `↓` pasa a la lista de resultados.

### 7.3 Listas (`ListBox`)

- Fondo transparente, sin borde ni *padding*; `ListBoxItem` sin *padding* ni altura mínima (la plantilla pone
  los márgenes).
- Seleccionado `#33507a`; *hover* `#2d2e33`. Listas que no son selección (diffs) anulan ambos con
  `ListBox.diff`.
- Plantilla típica de fila de archivo: `Grid ColumnDefinitions="22,*,Auto"` → letra/glifo de estado,
  directorio `dim` + nombre, acciones `flat`.
- Listas de navegación (resultados, marcadores, archivos grandes) abren con un clic.
- Listas grandes se reemplazan enteras (`IReadOnlyList` nuevo) en vez de vaciar y rellenar.

### 7.4 Árbol (`TreeView`)

Altura mínima de ítem 22 px, 12 px de fuente, chevron con `Margin="6,0,4,0"`, mismos colores de selección
que las listas. Carpetas con glifo `■` ámbar; archivos con `●` del color del lenguaje. Carga perezosa (un
hijo "…" hasta expandir) y orden: carpetas primero, alfabético sin distinguir mayúsculas. Se ocultan
carpetas de herramientas y build (`.git`, `node_modules`, `bin`, `obj`, `dist`…).

### 7.5 Pestañas

- **De ventana:** ver [6](#6-anatomía-de-la-ventana). La Home no es una pestaña: es el botón fijo `Button.home`; Ctrl+1 la selecciona.
- **De documento** (dentro de una vista, `ListBox.doctabs`): fondo de la franja `#1e1f22`, separador de 1 px
  entre pestañas, la seleccionada toma el color de la cabecera de abajo (`#26272b`) y texto blanco, para
  que parezca "pegada" al contenido. Primera pestaña fija ("Resumen"). Menú contextual: Cerrar los demás,
  Cerrar todos. Prefijo en el título: `● ` cambios sin guardar, `✎ ` en edición. Si el archivo tiene
  cambios de git, el título toma el color de su estado ([7.15](#715-estado-de-git-solo-lectura)).
- **Desbordamiento:** toda franja de pestañas (de ventana o de documento) lleva la clase `ListBox.tabstrip`.
  La barra horizontal ocupa **su propia fila de 4 px debajo** de las pestañas (`AllowAutoHide=False`), sin
  flechas y sin crecer al pasar el mouse, así nunca tapa ni roba clics a las pestañas. La rueda del mouse
  desplaza la franja de lado (`Views/TabStrip.cs`, instalado una vez en `App`). La pestaña seleccionada se
  desplaza a la vista sola.
- **Pestaña de vista previa** (como VS Code): un clic en el árbol, un salto desde la búsqueda, un marcador o
  el historial abre el archivo en una única pestaña con el título en *cursiva* (`TextBlock.preview`), que el
  siguiente archivo reemplaza. Doble clic (en el árbol o en la pestaña) o empezar a editar la deja fija.

### 7.6 Cabecera de documento/sección

Franja `#26272b` con borde inferior y `Padding="10,5"`: a la izquierda la ruta (dir `dim` + nombre SemiBold),
un badge del tipo (borde `#3a3c42`, fondo `#2d2e33`, texto del color del lenguaje, 11 px) y metadatos `dim`
11 px; a la derecha las acciones, terminando en un botón `⋯` con `MenuFlyout` para lo secundario.

### 7.7 Tarjetas y *stat tiles*

Tarjeta: `#222327`, borde 1 px, `CornerRadius=4`, título `TextBlock.section`. *Stat tile*: `#26272b`,
valor 20 px SemiBold, etiqueta `dim` 11 px debajo, `MinWidth=150`, en `WrapPanel`.

### 7.8 Métricas clave-valor

`Grid ColumnDefinitions="*,Auto"`, etiqueta `dim` 12 px, valor 12 px alineado a la derecha. Un valor fuera de
rango se pinta en ámbar y explica el criterio en `ToolTip`. Si el número es heurístico, el tooltip lo dice
("Aproximado: …").

### 7.9 Estados vacíos

Centrados, no interactivos (`IsHitTestVisible=False`): logo 64 px al 85 % de opacidad (opcional), título 16 px,
frase `dim` que dice qué hacer ("Agrega uno, escanea una carpeta o arrastra carpetas a esta ventana.").
En paneles pequeños: una línea `dim` 12 px ("Sin marcadores").

### 7.10 Diálogos

Construidos en código (`Views/Dialogs.cs` o `DialogService`), modales, fondo `#26272b`, sin redimensionar,
`SizeToContent=Height`, centrados en la ventana, fuera de la barra de tareas.

| Diálogo | Contenido | Teclas |
|---|---|---|
| `Confirm(título, mensaje, ok)` | Texto + [ok (accent)] [Cancelar] | Esc cancela |
| `Choose(…, a, b)` | Dos opciones + Cancelar | Esc cancela |
| `Message(título, texto)` | `TextBlock` con ajuste de línea dentro de un `ScrollViewer` (alto máx. 320) + [Cerrar]. **Nunca** un `TextBox`, ni siquiera de solo lectura. | Esc/Enter cierra |
| `About(...)` ("Acerca de", F1 y botón `?`) | Logo 48 px + nombre 18 SemiBold + "Versión x.y.z" `dim` + una frase de descripción · separador · "Atajos de teclado" en tabla de dos columnas (gesto monoespaciado `dim` 12 px, ancho 215 · descripción 12 px) agrupada por General/vista · separador · **Idioma** con un conmutador `Button.mode` (English \| Español) y, si cambia, la nota ámbar 11 px "se aplica la próxima vez…" · "Configuración guardada en: …" `dim` 11 px · [Abrir carpeta de configuración] [Cerrar]. Nada más. | Esc/Enter cierra |
| `Form` / `Prompt` | Etiquetas `dim` sobre cada campo; primer campo enfocado y seleccionado | Enter acepta, Esc cancela |
| `QuickOpen` / `PickPath` | Buscador arriba + lista sobre `#1e1f22`; nombre SemiBold y ruta `dim` 11 px | ↑/↓ mueve, Enter abre, Esc cierra |

El botón principal va primero (a la izquierda del grupo alineado a la derecha) y es `accent`.

### 7.11 Menús contextuales

Verbos en infinitivo o imperativo corto, separadores para agrupar, lo destructivo al final tras un
separador ("Quitar de la lista"). Las opciones que abren otra ventana terminan en `...`.

### 7.12 Barra de estado

Izquierda: la **rama de git** si la vista es de un repositorio ([7.15](#715-estado-de-git-solo-lectura)),
después el último aviso (`Notice`) o texto de ocupado; nunca diálogos para información no crítica.
Derecha: contexto del elemento actual (posición del cursor, indentación, fin de línea, codificación, lenguaje
en su color, modo "Solo lectura" `dim` / "Editando" ámbar SemiBold), toggles `flat` y tamaño de fuente.

### 7.13 Conmutador de vista (`Button.mode`)

Dos botones pegados en la cabecera del documento, a la izquierda de las acciones: **Renderizada | Código**.
Izquierdo `CornerRadius="3,0,0,3"`, derecho `"0,3,3,0"` sin borde izquierdo (`BorderThickness="0,1,1,1"`),
`Padding="10,4"`, 12 px. Inactivo: fondo de botón normal y texto `Dim`; activo (`Classes.active`): `#33507a`
y texto blanco (hover `#3a5a88`). Solo aparece si el archivo admite vista renderizada. Úsalo para cualquier
alternancia entre dos presentaciones del mismo contenido.

### 7.14 Vista renderizada de documentos

Los documentos (`.md`, `.markdown`, `.mdx`, `.txt`, `.text`) se abren **renderizados** por defecto
(si pesan ≤ 2 MB); el código fuente queda a un clic. Se construyen con controles nativos (Markdig →
Avalonia), nunca con un navegador embebido.

| Elemento | Estilo |
|---|---|
| Página | Fondo `#1e1f22`, contenido con `MaxWidth=920`, `Padding="40,28,40,60"` |
| Párrafo | Segoe UI 14, interlineado 22, `Text`, margen inferior 12; seleccionable (`SelectableTextBlock`, selección `#33507a`) |
| H1 / H2 | 26 / 21 px SemiBold, con línea inferior 1 px `#3a3c42` |
| H3 / H4 / H5-6 | 17 / 15 / 14 px SemiBold (H5-6 en `Dim`) |
| Código en línea | Monoespaciada 12.5, fondo `#2d2e33`, texto ámbar `#e0a24a` |
| Bloque de código | Fondo `#1b1c1f`, borde 1 px, radio 4, `Padding="14,10"`, 12.5 px, scroll horizontal; lenguaje en `Dim` 10 px arriba a la derecha |
| Enlace | `#7db4ea` subrayado, cursor mano, tooltip con la URL. Relativo → abre el archivo en la app; `#ancla` → desplaza; `http(s)`/`mailto` → aplicación predeterminada |
| Cita | Borde izquierdo 3 px `#3a3c42`, texto `Dim` |
| Listas | Viñeta `•` o número en `Dim`, columna de 22 px; tareas `☑` verde / `☐` `Dim` |
| Tabla | Celdas con borde 1 px `#3a3c42`, `Padding="10,5"`, cabecera SemiBold sobre `#26272b`; columnas a contenido (máx. 340) y la última ocupa el resto con ajuste de línea |
| Separador | 1 px `#3a3c42` |
| Imágenes locales | Tamaño natural, se reducen al ancho disponible; las remotas no se cargan (se muestra el texto alternativo) |
| HTML crudo | Se muestra como texto `Dim` monoespaciado; nunca se ejecuta |

Texto plano: párrafos separados por líneas en blanco, con el mismo estilo de párrafo. La barra de estado dice
"Vista renderizada" en lugar de la posición del cursor. Saltar desde el outline desplaza la vista renderizada
al bloque; un resultado de búsqueda (que necesita la coincidencia exacta) cambia a **Código**. "Editar"
siempre pasa a Código. Los marcadores TODO/FIXME en prosa solo cuentan si abren la línea y nunca dentro de
`código en línea`.

### 7.15 Estado de git (solo lectura)

Las apps que **no** son el cliente de git (TextPeach) muestran el estado como la mayoría de los editores, pero
nunca ejecutan una acción de git: solo `git --no-optional-locks status --porcelain=v1 -z --branch`
(`Services/GitStatus.cs`), que ni siquiera refresca el índice. Sin git o fuera de un repositorio, no se muestra
nada.

| Estado | Color | Letra |
|---|---|---|
| Modificado | Ámbar `#e0a24a` | `M` |
| Sin seguimiento | Verde `#6cc070` | `U` |
| Agregado | Verde `#6cc070` | `A` |
| Renombrado | Turquesa `#4ac7c0` | `R` |
| Eliminado | Rojo `#e0645a` | `D` |
| Conflicto | Rosa `#d96a9b` | `!` |
| Ignorado | `Dim` | — |

- **Archivos** (árbol, lista del filtro, título de la pestaña): el nombre en el color del estado y la letra
  a la derecha (`TextBlock.gitstatus`: 11 px SemiBold, margen `8,0`). Sin cambios, el nombre queda en `Text`.
- **Carpetas:** nombre en el color del cambio más fuerte que contienen (conflicto > modificado > eliminado >
  renombrado > agregado > sin seguimiento) y un `•` a la derecha. Lo ignorado no se propaga.
- **Rama** en la barra de estado, a la izquierda: `Button.flat.branch` con un icono de rama dibujado
  (`Path.branch`, trazo `Dim` 1.2) + nombre `#a9acb2` + `↑n ↓n` + "n cambios" `dim`. Tooltip con rama, commits
  por enviar/traer, archivos con cambios y "Clic para abrir el repositorio en GitPeach". *HEAD* separado: el
  commit corto. **Un clic abre GitPeach** (nunca cambia de rama aquí); si no está instalado, el mismo diálogo
  que usa GitPeach cuando no encuentra TextPeach ([8.7](#87-integración-con-el-sistema)).
- Se actualiza con *debounce* (600 ms) ante cualquier cambio de la carpeta o de `.git` (salvo `*.lock`) y con
  F5.

- **Cambios dentro del archivo** (contra el último commit, `git show HEAD:<ruta>`, nunca se escribe nada):
  barra de 3 px entre los números de línea y el texto (`GitChangeMargin`): verde `#6cc070` agregado, ámbar
  `#e0a24a` modificado, cuña roja `#e0645a` donde se borraron líneas. Un archivo sin seguimiento es todo verde.
- **Regla junto al scroll** (`OverviewRuler`, 10 px): el archivo entero a escala; mitad izquierda los cambios
  de git, a la derecha las coincidencias de la búsqueda (`#ffd27a`) y una línea `Text` en el cursor. Un clic
  salta ahí.
- **Diff rápido** (`ToggleButton.diff` "Cambios +n −n" en la cabecera del archivo, solo si hay cambios; `Alt+D`):
  reemplaza la vista por el diff con los colores de [3.6](#36-diffs), conmutador *En línea / Lado a lado*
  (se recuerda), `↑ ↓` entre cambios, y abre en el primer cambio.

### 7.15b Proyectos sin git

Cualquier carpeta puede ser un proyecto. Sin git solo faltan los extras de git: la barra de estado dice
"Sin git" en `dim` (tooltip con el motivo) y el **Resumen** muestra arriba una tarjeta de aviso
(`Border.warning`: fondo `#2d2e33`, borde ámbar `#8a6d1f` con 3 px a la izquierda, `⚠` ámbar) con el motivo:
no es un repositorio, git no está instalado o git no respondió.

### 7.16 Barras de desplazamiento

**Una sola barra para todo** (listas, árbol, editor, documentos, diálogos y franjas de pestañas), definida en
`Styles.axaml`: línea fija de **4 px** (`ScrollBarSize` = 4) con esquinas de 2 px, **sin flechas** y que **no
crece** al pasar el mouse. Todo `ScrollViewer` usa `AllowAutoHide=False`, así la barra ocupa su propia fila o
columna y nunca queda encima del contenido ni le roba clics. En franjas de pestañas (`ListBox.tabstrip`,
[7.5](#75-pestañas)) la rueda del mouse además desplaza de lado. No se definen barras distintas por vista.

---

## 8. Patrones de interacción

### 8.1 Home de "proyectos" (repos, carpetas…)

Primera pestaña de toda app Peach que trabaje con varios elementos:

- Toolbar: **Agregar...** (accent), **Escanear carpeta...**, **Nuevo grupo...**, **Actualizar**, filtro
  que ocupa el resto y, a la derecha, `▾ Expandir` / `▸ Contraer` (flat) y `★ Favoritos`.
- Lista: ★ ámbar · nombre 14 px SemiBold · dato de contexto en verde (rama, tipo de proyecto) · aviso rojo
  "(carpeta no encontrada)" · ruta `dim` 12 px · fecha `yyyy-MM-dd HH:mm` `dim` a la derecha.
- Orden: favoritos primero, luego último abierto. Filtro difuso (letras en orden) sobre nombre + ruta.
- Doble clic o Enter abre; Supr quita de la lista (multiselección con Ctrl/Shift).
- "Quitar" nunca toca el disco, y el aviso lo aclara.

**Grupos** (lógica compartida en `RepoGrouping` / `ProjectGrouping`, funciones puras y testeadas):

- **Escanear carpeta** busca los elementos dentro de la carpeta elegida y los guarda en un grupo con el
  nombre de esa carpeta (lo crea si no existe). Los que ya estaban en otro grupo se quedan donde estaban, y el
  aviso lo cuenta: `Grupo "X": 5 proyectos encontrados, 2 nuevos, 1 ya estaba en otro grupo.`
- Arrastrar a la ventana una carpeta que solo contiene elementos equivale a escanearla.
- Cabecera de grupo: franja `#222327` con bordes superior e inferior, chevron `▸`/`▾` `Dim` 16 px, nombre
  SemiBold, contador `dim` 12 px ("3 proyectos") y "Abrir todos" `flat` a la derecha. Clic alterna plegado.
  Los elementos de un grupo se sangran 26 px.
- Sin ningún grupo creado, la lista es plana (sin cabeceras). Con grupos, lo no agrupado aparece al final
  bajo "Sin grupo".
- Mientras hay filtro, todo se muestra expandido y se ocultan los grupos vacíos.
- El estado de plegado se guarda (`Collapsed` por grupo, `UngroupedCollapsed`).
- Menú de grupo: Abrir todos (n) · Expandir/Contraer · (sep) · Renombrar grupo… · Eliminar grupo (quedan sin
  grupo)… · Quitar el grupo y sus elementos de la lista….
- Menú de elemento(s): Abrir / Abrir los n · Favorito · Renombrar · (sep) · **Mover a grupo ▸** (grupos
  existentes, sep, Nuevo grupo…) · Quitar del grupo · (sep) · acciones externas · (sep) · Quitar de la lista.
- Los menús los produce el view model como `MenuAction` (con submenús) y los dibuja `MenuPresenter`.

### 8.2 Pestañas persistentes

Cada elemento abierto es una pestaña; se guardan las abiertas y la activa, y se restauran al iniciar (si la
carpeta sigue existiendo). Dentro de una pestaña se restaura también su sesión (archivos abiertos y el
activo). Cerrar con cambios sin guardar pide confirmación.

### 8.3 Ocupado y avisos

`IsBusy`/`BusyText` muestran progreso indeterminado en la barra de estado; las acciones que no deben
solaparse se deshabilitan con `IsEnabled="{Binding !IsBusy}"`. El resultado de una acción va a `Notice`
("fetch completado", "Copiado: …"). Los errores con detalle técnico abren `Message` además del aviso.

### 8.4 Vigilancia del disco

Un `FileSystemWatcher` por pestaña con *debounce* (300 ms para un archivo, 600–700 ms para estructura),
ignorando carpetas pesadas. Al cambiar algo se refresca solo lo afectado y se conservan selección, expansión,
cursor y scroll. Si el usuario está editando, no se pisa su trabajo: aparece el banner informativo con
**Recargar**.

### 8.5 Solo lectura primero (apps de revisión)

Abrir nunca habilita edición. "✎ Editar" (`warn`, `Ctrl+E`) cambia a modo edición: banner ámbar
explicativo, **Guardar** (`accent`, `Ctrl+S`) y **Terminar edición**. Terminar con cambios pregunta
Guardar / Descartar / Cancelar. Se guarda con la misma codificación y BOM con la que se leyó.

### 8.5b Seguir lo que cambia en el disco

Pensado para mirar lo que hace un agente de IA. La barra lateral tiene una tercera sección, **Cambios** (con
un punto ámbar si hay algo), que lista los archivos creados (`+` verde), modificados (`~` ámbar) o borrados
(`−` rojo, nombre tachado `TextBlock.gone`) desde que se abrió el proyecto, el más reciente arriba, con
"hace n min". **Seguir** (`ToggleButton.follow`, punto gris que se enciende verde) abre solo, en la pestaña
de vista previa, cada archivo que cambia y salta al cambio; nunca le quita el foco a una edición en curso.

### 8.5c Historial y carpetas ignoradas

- `Alt+←` / `Alt+→` (y los botones `←` `→` al inicio de la toolbar) vuelven a donde estabas antes de saltar
  desde la búsqueda, la estructura, un marcador, "Ir a línea" o la regla.
- La vista renderizada o de código elegida se recuerda **por archivo**. Las carpetas a ignorar se eligen
  **por proyecto** (menú contextual del árbol: "Ignorar esta carpeta" y "Carpetas ignoradas…"); quedan fuera
  del árbol, la búsqueda y la lista de cambios.

### 8.6 Búsqueda

- **Rápida por nombre** (`Ctrl+P`): ranking nombre exacto > prefijo del nombre > contiene en nombre >
  contiene en ruta > difuso; empates por ruta más corta.
- **De contenido** (`Ctrl+Shift+F`): búsqueda en vivo con *debounce* de 350 ms, opciones `Aa`/`ab|`/`.*`,
  filtro de inclusión por glob, resultados agrupados por archivo (cabecera con contador *pill*) y fragmento
  con la coincidencia resaltada; límite de resultados indicado ("(limitado)") y tiempo empleado.
- Si hay texto seleccionado al abrir la búsqueda, se usa como consulta.

### 8.7 Integración con el sistema

**TextPeach** solo ofrece "Abrir con la aplicación predeterminada" (`Process.Start` con `UseShellExecute=true`): en la toolbar, en el menú `⋯` del archivo, en la nota de binarios y en el menú de la Home (para la carpeta). Los enlaces `http(s)`/`mailto` de los documentos también se delegan al sistema.

**GitPeach** ofrece Explorador y Terminal (herramientas del sistema) y **TextPeach** en lugar de cualquier editor externo: el botón "TextPeach" de la toolbar abre el repositorio, y "Abrir en TextPeach" del menú de un archivo abre el repositorio con ese archivo.

**TextPeach** delega todo lo de git en **GitPeach**: un clic en la rama de la barra de estado ejecuta `GitPeach.exe <repositorio>`.

Cada app busca a la otra (`TextPeachLocator` / `GitPeachLocator`, espejos uno del otro) en este orden: el
`location.json` que la otra anotó al abrirse ([8.8](#88-carpeta-compartida-peachapps)), junto al propio exe,
en una carpeta hermana `..\<App>`, en `%LOCALAPPDATA%\Programs\<App>`, en `%ProgramFiles%\<App>` y en el
`PATH`. Si no está, un diálogo dice qué es y ofrece **Ir al repositorio** (accent) o **Por ahora no**, con el
mismo texto en ambas apps ("<App> es el … que acompaña a <la otra>, y no se encontró en este equipo. Si ya lo
tienes, ábrelo una vez desde donde esté y <la otra> lo reconocerá solo…").

Ambas aceptan rutas por línea de comandos (`App.exe <carpeta> [archivo]`) y son de **instancia única**: una
segunda ejecución entrega sus rutas a la ventana abierta por una *named pipe* (`Services/SingleInstance.cs`,
mutex y pipe por usuario) y termina, así las apps se pueden pedir cosas entre sí sin crear ventanas duplicadas
ni pisar el estado guardado.

### 8.8 Carpeta compartida `peachapps`

Todas las apps Peach guardan su configuración en una carpeta común (`Services/PeachApps.cs`, el mismo archivo
en cada app con otro *namespace*):

```
%APPDATA%\peachapps\
  GitPeach\    state.json · location.json
  TextPeach\   state.json · location.json
```

- `state.json`: el estado de la app (lista, pestañas, ventana, idioma, preferencias). En modo portable
  (`portable.txt` junto al exe) va en `.\data\` y no se usa la carpeta compartida.
- `location.json`: lo escribe la app **cada vez que se abre** (también en modo portable), para que las demás
  la encuentren aunque se haya movido:

  ```json
  { "exe": "D:\\Apps\\TextPeach\\TextPeach.exe", "version": "1.0.0", "lastRun": "2026-10-20T20:41:07-03:00" }
  ```

  Se escribe a un `.tmp` y se renombra (nunca queda a medias). Las compilaciones Debug no lo escriben, para no
  reemplazar la ubicación de la versión real. Quien lo lee solo confía en `exe` si ese archivo sigue
  existiendo.
- La primera vez se migra la configuración de la ubicación antigua (`%APPDATA%\<App>\`).
- "Acerca de" muestra la carpeta real y tiene **Abrir carpeta de configuración**.

---

## 9. Teclado

Convenciones compartidas (no las reutilices para otra cosa):

| Atajo | Acción |
|---|---|
| `Ctrl+P` | Búsqueda rápida (repo/proyecto; en TextPeach, archivo si hay un proyecto activo) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Pestaña siguiente / anterior |
| `Ctrl+1 … Ctrl+9` | Ir a la pestaña N |
| `Ctrl+W` | Cerrar (documento actual; si no hay, la pestaña) |
| `F1` | "Acerca de" con los atajos y el idioma (botón `?` en la barra de pestañas) |
| `F5` | Refrescar |
| `Ctrl+F` | Buscar dentro de la vista actual |
| `Ctrl+Shift+F` | Búsqueda amplia (en TextPeach, en todo el proyecto) |
| `Ctrl+Enter` | Confirmar el formulario principal (commit) |
| `Ctrl+S` | Guardar (solo cuando hay edición) |
| `Esc` | Cerrar diálogo / limpiar búsqueda |
| `Enter` / `Supr` en listas | Abrir / quitar |

Los atajos se declaran en XAML (`KeyBindings` de la ventana o de la vista) y **todos** se listan en
`ViewModels/Shortcuts.cs`, que alimenta el diálogo F1. Si un atajo es de una vista, algo dentro de esa vista
debe tener el foco al abrirla. Los atajos se muestran también en `ToolTip.Tip` de su botón.

---

## 10. Textos y tono

- **Dos idiomas, inglés y español.** Ningún texto visible va escrito en XAML ni en C#: todos viven en
  `Services/Strings.cs` como par (inglés, español) bajo una clave por pantalla (`home.add`, `pv.goToFile`,
  `git.modified`…). En XAML `Text="{l:L clave}"` (con `xmlns:l="using:<App>.Services"`), en C# `Loc.T("clave")`,
  `Loc.F("clave", args)` y `Loc.Plural("clave", n)` (busca `clave.one` / `clave.many`). Un test verifica que
  cada clave usada exista y que ambos idiomas tengan los mismos marcadores `{0}`.
- El idioma se resuelve una vez al iniciar (`Loc.Init`, antes de crear vistas): el elegido en "Acerca de" o,
  si no hay, el de Windows (español si Windows está en español; inglés en otro caso). Las apps usan
  globalización invariante, así que el idioma del sistema se pide a Windows (`GetUserDefaultUILanguage`).
  Cambiarlo se aplica al reiniciar.
- Lo que no se traduce: nombres de apps, atajos (`Ctrl+P`), términos técnicos (stage, commit, TODO, CRLF),
  rutas y nombres de lenguajes.
- Inglés: claro y corto, *sentence case* ("Search project", "Open with the default app").
- Español neutro, tuteo ("Agrega uno…", "Elige un archivo…"). Sin exclamaciones ni emojis.
- Mayúscula solo al inicio (*sentence case*): "Buscar en proyecto", "Abrir con la aplicación predeterminada".
- Términos técnicos del dominio se dejan como se usan (stage, commit, fetch, hunk, TODO).
- Títulos de ventana y pestañas: el nombre del elemento, no el de la app.
- Avisos en pasado o descriptivos: "Agregado: X", "Quitado de la lista: X (no se borró nada del disco)",
  "Escaneo terminado: 4 proyectos encontrados, 2 nuevos.".
- Errores: qué pasó + dato concreto ("La carpeta ya no existe: C:\…").
- Notas de contenido en cursiva `dim`: "Archivo binario no mostrado (2.3 MB)", "Sin resultados".
- Números con separador de miles (`N0`), tamaños con `B/KB/MB` y un decimal, fechas `yyyy-MM-dd HH:mm`.

---

## 11. Iconografía y marca

- **Glifos Unicode** en lugar de una fuente de iconos: `★` favorito, `✕` cerrar, `●` tipo/sin guardar,
  `■` carpeta, `✎` editar, `⋯` más acciones, `↶` descartar, `⧉` abrir en ventana, `¶` espacios
  invisibles, `?` ayuda, `+`/`−` stage/unstage. Letras para tipos de símbolo: `N` namespace, `C` clase,
  `I` interfaz, `S` struct, `E` enum, `R` record, `ƒ` función, `M` método, `P` propiedad, `#` título,
  `K` clave, `{}` selector, `T` tabla, `<>` elemento.
- **Logo:** una fruta con hoja verde y una figura blanca de trazo redondeado que dice qué hace la app.
  Cada app tiene su propio color de fruta para distinguirlas en la barra de tareas:
  - GitPeach: durazno naranja-rosa (`#ffb668 → #ee4662`) con ramas de git.
  - TextPeach: ciruela violeta (de `#3b3790` a `#c08ef0`, la mitad izquierda más oscura) con `</>`.
  El icono va en `app.ico` (16–256 px, fondo transparente), `icon64.png` y `logo.png`, y se muestra en la
  barra de título, en "Acerca de" y en los estados vacíos; **nunca** en la barra de pestañas. Es el único
  lugar con degradados y colores fuera de la paleta.
- El nombre siempre es `<Algo>Peach` en una palabra, con P mayúscula.

---

## 12. Datos, métricas y gráficos

- Preferir **barras horizontales** simples (una por categoría, alto 8 px, esquina derecha redondeada 4 px,
  ancho proporcional al máximo) sobre tortas o gráficos complejos. Junto a la barra: porcentaje SemiBold,
  valor absoluto `dim` y conteo secundario `dim`.
- Color de barra = color de la categoría (serie [3.7](#37-series-categóricas-grafo-lenguajes-gráficos)).
- Los indicadores heurísticos (comentarios, complejidad) se etiquetan como aproximados en el tooltip.
- Umbrales de advertencia en ámbar (TextPeach): línea > 160 caracteres, anidación > 6, decisiones > 150,
  indentación o fin de línea mixtos, espacios al final.

---

## 13. Rendimiento percibido

- Todo lo que toca disco o procesos va en `Task.Run` y vuelve al hilo de UI solo para asignar resultados.
- Reemplazar colecciones completas en una sola asignación; cachés pequeñas (LRU) para vistas costosas.
- *Debounce* en filtros (350 ms), watchers (300–700 ms) y análisis mientras se edita (800 ms).
- Límites explícitos y comunicados: diffs a 20 000 filas, búsqueda a 3 000 coincidencias, archivos de texto
  a 16 MB, listado de proyecto a 60 000 archivos.
- Mostrar "Cargando…" en `dim` en lugar de dejar el panel vacío.

---

## 14. Plantillas de código

### 14.1 `App.axaml`

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="MiApp.App"
             RequestedThemeVariant="Dark">
  <Application.Styles>
    <FluentTheme />
    <!-- Solo si usa AvaloniaEdit: -->
    <StyleInclude Source="avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml" />
    <StyleInclude Source="/Styles.axaml" />
  </Application.Styles>
</Application>
```

### 14.2 `Styles.axaml` base

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Styles.Resources>
    <x:Double x:Key="TreeViewItemMinHeight">22</x:Double>
    <SolidColorBrush x:Key="TreeViewItemBackgroundPointerOver" Color="#2d2e33" />
    <SolidColorBrush x:Key="TreeViewItemBackgroundSelected" Color="#33507a" />
    <SolidColorBrush x:Key="TreeViewItemBackgroundSelectedPointerOver" Color="#3a5a88" />
    <SolidColorBrush x:Key="ToggleButtonBackgroundChecked" Color="#33507a" />
    <SolidColorBrush x:Key="ToggleButtonBackgroundCheckedPointerOver" Color="#3a5a88" />
  </Styles.Resources>

  <Style Selector="Window">
    <Setter Property="Background" Value="#1e1f22" />
    <Setter Property="FontFamily" Value="Segoe UI" />
    <Setter Property="FontSize" Value="13" />
  </Style>

  <Style Selector="Button">
    <Setter Property="Background" Value="#2d2e33" />
    <Setter Property="Padding" Value="10,4" />
    <Setter Property="CornerRadius" Value="3" />
    <Setter Property="BorderBrush" Value="#3a3c42" />
    <Setter Property="BorderThickness" Value="1" />
  </Style>
  <Style Selector="Button:pointerover /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Background" Value="#3a3c42" />
  </Style>
  <Style Selector="Button.flat">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Padding" Value="6,2" />
  </Style>
  <Style Selector="Button.accent">
    <Setter Property="Background" Value="#4a90d9" />
    <Setter Property="Foreground" Value="White" />
    <Setter Property="BorderThickness" Value="0" />
  </Style>
  <Style Selector="Button.accent:pointerover /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Background" Value="#5aa0e9" />
  </Style>
  <Style Selector="Button.warn">
    <Setter Property="Background" Value="#5a4318" />
    <Setter Property="BorderBrush" Value="#8a6d1f" />
  </Style>

  <Style Selector="TextBox">
    <Setter Property="Background" Value="#26272b" />
    <Setter Property="BorderBrush" Value="#3a3c42" />
  </Style>

  <Style Selector="ListBox">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Padding" Value="0" />
    <Setter Property="BorderThickness" Value="0" />
  </Style>
  <Style Selector="ListBoxItem">
    <Setter Property="Padding" Value="0" />
    <Setter Property="MinHeight" Value="0" />
  </Style>
  <Style Selector="ListBoxItem:selected /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Background" Value="#33507a" />
  </Style>
  <Style Selector="ListBoxItem:pointerover:not(:selected) /template/ ContentPresenter#PART_ContentPresenter">
    <Setter Property="Background" Value="#2d2e33" />
  </Style>

  <Style Selector="GridSplitter">
    <Setter Property="Background" Value="#3a3c42" />
  </Style>
  <Style Selector="TextBlock.dim">
    <Setter Property="Foreground" Value="#8a8d93" />
  </Style>
  <Style Selector="TextBlock.mono">
    <Setter Property="FontFamily" Value="Cascadia Mono, Consolas, Courier New" />
  </Style>
  <Style Selector="TextBlock.section">
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="VerticalAlignment" Value="Center" />
  </Style>
</Styles>
```

Las clases `seg`, `chip`, `doctabs` y el estilo del editor están en `src/TextPeach/Styles.axaml`.

### 14.3 `Controls/Palette.cs`

```csharp
public static class Palette
{
    private static SolidColorBrush B(string hex) => new(Color.Parse(hex));

    public static readonly IBrush Bg = B("#1e1f22"), Chrome = B("#18191c"), Sidebar = B("#222327");
    public static readonly IBrush Panel = B("#26272b"), Panel2 = B("#2d2e33"), Border = B("#3a3c42");
    public static readonly IBrush Code = B("#1b1c1f"), FileBg = B("#34363c");
    public static readonly IBrush Text = B("#d4d4d4"), ContextFg = B("#c8c8c8"), Dim = B("#8a8d93");
    public static readonly IBrush Accent = B("#4a90d9"), Selection = B("#33507a");

    public static readonly IBrush Green = B("#6cc070"), Red = B("#e0645a"), Amber = B("#e0a24a");
    public static readonly IBrush Blue = B("#4a90d9"), Purple = B("#9a7ae0"), Teal = B("#4ac7c0");
    public static readonly IBrush Pink = B("#d96a9b"), Olive = B("#c8c84a");

    public static readonly IBrush AddedBg = B("#1d3a2a"), AddedFg = B("#a6e3b4");
    public static readonly IBrush RemovedBg = B("#431f22"), RemovedFg = B("#f4a6a6");
    public static readonly IBrush HunkBg = B("#25364d");

    public static readonly Color[] Series =
    {
        Color.Parse("#4a90d9"), Color.Parse("#e0a24a"), Color.Parse("#6cc070"), Color.Parse("#d96a9b"),
        Color.Parse("#9a7ae0"), Color.Parse("#4ac7c0"), Color.Parse("#e0645a"), Color.Parse("#c8c84a"),
    };
}
```

### 14.4 Esqueleto de vista de trabajo

```xml
<DockPanel>
  <Border DockPanel.Dock="Top" Background="#26272b" BorderBrush="#3a3c42" BorderThickness="0,0,0,1" Padding="8,6">
    <DockPanel>
      <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" Spacing="4">
        <Button Content="Abrir con la app predeterminada" IsEnabled="{Binding HasFile}" />
      </StackPanel>
      <StackPanel Orientation="Horizontal" Spacing="4">
        <Button Content="Acción" />
        <Border Width="1" Background="#3a3c42" Margin="6,2" />
        <Button Content="Refrescar" ToolTip.Tip="F5" />
        <TextBlock Text="{Binding ContextText}" FontWeight="SemiBold" Foreground="#6cc070" VerticalAlignment="Center" Margin="14,0,0,0" />
      </StackPanel>
    </DockPanel>
  </Border>

  <Border DockPanel.Dock="Top" Background="#5a4318" Padding="10,6" IsVisible="{Binding HasWarning}">
    <TextBlock Text="{Binding WarningText}" FontWeight="SemiBold" />
  </Border>

  <Border DockPanel.Dock="Bottom" Background="#18191c" BorderBrush="#3a3c42" BorderThickness="0,1,0,0" Padding="10,3">
    <DockPanel>
      <ProgressBar DockPanel.Dock="Right" IsIndeterminate="True" IsVisible="{Binding IsBusy}" Width="100" Height="6" />
      <TextBlock Classes="dim" Text="{Binding Notice}" TextTrimming="CharacterEllipsis" />
    </DockPanel>
  </Border>

  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="240" MinWidth="140" />
      <ColumnDefinition Width="4" />
      <ColumnDefinition Width="*" MinWidth="320" />
      <ColumnDefinition Width="4" />
      <ColumnDefinition Width="300" MinWidth="200" />
    </Grid.ColumnDefinitions>
    <Border Background="#222327"><!-- sidebar --></Border>
    <GridSplitter Grid.Column="1" ResizeDirection="Columns" />
    <!-- contenido -->
    <GridSplitter Grid.Column="3" ResizeDirection="Columns" />
    <Border Grid.Column="4" Background="#222327"><!-- detalle --></Border>
  </Grid>
</DockPanel>
```

### 14.5 Estructura de carpetas

```
MiApp/
  MiApp.sln · build.cmd · dev.cmd · .gitignore
  docs/            DISENO.md (este archivo) · ARQUITECTURA.md
  src/MiApp/
    App.axaml(.cs) · Program.cs · Styles.axaml · app.manifest · MiApp.csproj
    Assets/        app.ico · icon64.png · (peach-dark.json)
    Controls/      Palette.cs · controles dibujados a mano
    Models/        datos
    Services/      estado (Store), escáneres, lógica pura · PeachApps · SingleInstance · Loc + Strings
    ViewModels/    TabViewModel · MainWindowViewModel · HomeViewModel · <Elemento>ViewModel · RowModels · Shortcuts
    Views/         MainWindow · HomeView · <Elemento>View · Dialogs
  tests/MiApp.Tests/
```

---

## 15. Checklist para una app nueva

- [ ] Nombre `<Algo>Peach`, logo durazno con figura propia, `app.ico` + `icon64.png`.
- [ ] Avalonia 11.3 Fluent oscuro, .NET 10, CommunityToolkit.Mvvm, bindings compilados, render software.
- [ ] `Styles.axaml` y `Palette.cs` copiados de esta guía; ningún color fuera de la paleta.
- [ ] Ventana: pestañas `#18191c` con icono + `?`, Home no cerrable primero, `PlaceWindow` copiado.
- [ ] Home de elementos con agregar/escanear/filtro/favoritos/arrastrar/menú contextual.
- [ ] Vista de trabajo: toolbar `#26272b`, banners, 3 columnas con splitters, barra de estado `#18191c`.
- [ ] Un solo botón `accent` por zona; acciones riesgosas con confirmación o `warn`.
- [ ] `state.json` con pestañas, sesión por pestaña, ventana y preferencias; `portable.txt`.
- [ ] Atajos comunes de [9](#9-teclado) + `Shortcuts.cs` + F1 + `Ctrl+1…9`.
- [ ] Textos en `Strings.cs` (inglés y español), *sentence case*, `...` en acciones con diálogo; selector de
      idioma en "Acerca de".
- [ ] `PeachApps.SettingsFolder` + `PeachApps.Announce` al iniciar; instancia única; locator de las apps
      hermanas.
- [ ] Franjas de pestañas con `ListBox.tabstrip`; `ScrollBarSize` 10.
- [ ] Trabajo pesado fuera del hilo de UI, *debounce* y límites comunicados.
- [ ] `build.cmd` (single-file portable) y `dev.cmd`; tests xUnit de la lógica pura.
- [ ] Si se agrega un patrón nuevo, documentarlo aquí.

---

## 16. Anexo: dónde se aplica cada regla

| Regla | GitPeach | TextPeach | RestPeach |
|---|---|---|---|
| Home de elementos | `HomeView` (repositorios, rama en verde) | `HomeView` (proyectos, tipo en verde: .NET, Node, Python…) | `HomeView` (proyectos; "N peticiones" en verde, "sin colección todavía" atenuado, tipo) |
| Pestaña de trabajo | `RepoView`: ramas │ historial con grafo │ cambios + diff | `ProjectView`: árbol/búsqueda │ archivos abiertos │ análisis | `ProjectView`: árbol de la colección │ peticiones, ejecuciones y entornos │ variables, cURL y sesión |
| Banner ámbar | Merge/rebase en curso | Modo edición | — (las variables sin definir se avisan en rojo debajo de la URL) |
| Banner azul | Cabecera de hunk | Archivo cambiado en disco | Petición cambiada en disco con cambios sin guardar |
| Letras de estado | `A M D R U` en archivos | `M U A R D !` de git en árbol y pestañas (solo lectura); `C I M ƒ #…` en el outline; `TODO FIXME…` en marcadores | `M U A R D !` de git en el árbol; método (`GET POST DEL…`) como etiqueta de cada petición y pestaña |
| Rama | Barra lateral y toolbar (con acciones) | Barra de estado, solo se muestra; clic → GitPeach | Barra de estado, solo se muestra; clic → GitPeach |
| Serie de colores | Carriles del grafo | Lenguajes (árbol, resumen) | Métodos HTTP |
| Búsqueda rápida | `Ctrl+P` repos | `Ctrl+P` archivos · `Ctrl+Shift+O` proyectos | `Ctrl+P` peticiones · `Ctrl+Shift+O` proyectos |
| Log/estado | Log de comandos git | Cursor, codificación, EOL, lenguaje, modo | Último envío o guardado, valores capturados, formato, ajuste de línea, zoom |
| Resumen con tarjetas | — | Pestaña "Resumen": tiles, barras por lenguaje, archivos más extensos, marcadores | Pestaña de ejecución: resumen en una línea y filas ✓/✗ |

## 17. Anexo RestPeach: colores y patrones propios

Todo sale de la paleta de la sección 3; aquí solo se fija **qué significa** cada color en RestPeach. El código
está en `Controls/Palette.cs` (`Look`).

### 17.1 Métodos HTTP

Son categorías, así que toman colores de la serie (3.7). DELETE conserva el rojo porque *borra*.

| Método | Color | Etiqueta |
|---|---|---|
| GET | verde `#6cc070` | `GET` |
| POST | ámbar `#e0a24a` | `POST` |
| PUT | azul `#4a90d9` | `PUT` |
| PATCH | violeta `#9a7ae0` | `PATCH` |
| DELETE | rojo `#e0645a` | `DEL` |
| HEAD, OPTIONS | turquesa `#4ac7c0` | `HEAD`, `OPT` |

La etiqueta (`TextBlock.method`) va en mayúsculas, monoespaciada, 9 px, negrita, con un ancho mínimo fijo
(árbol, pestañas, filas de una ejecución) para que los nombres queden alineados.

### 17.2 Estados de respuesta

| Rango | Texto (listas) | Insignia rellena (cabecera de la respuesta) |
|---|---|---|
| 2xx | verde `#6cc070` | `#2f6f3e` |
| 3xx | azul `#4a90d9` | `#2d5c8a` |
| 4xx | ámbar `#e0a24a` | `#8a6d1f` |
| 5xx y sin respuesta | rojo `#e0645a` | `#8a2f2a` |

Las insignias son pasos oscuros del color semántico con texto blanco, como las de referencias de GitPeach.

### 17.3 Origen de una variable

| Origen | Color |
|---|---|
| Colección | atenuado `#8a8d93` |
| Carpeta | ámbar |
| Entorno | azul |
| Secreto | violeta (y el valor siempre como `••••`) |
| Sesión (capturado) | turquesa |

### 17.4 Pruebas y ejecuciones

- `✓` verde = pasó, `✗` rojo = falló, `!` rojo = la línea no se pudo evaluar o no hubo respuesta, `=` turquesa
  = captura.
- El resumen de una ejecución es una sola línea de texto (`5 peticiones · 5 correctas · 916 ms`), verde si todo
  pasó y rojo si no; no se usan tarjetas.
- El detalle de un resultado usa los mismos conmutadores `Button.seg` que la respuesta de una petición:
  **Pruebas · Body · Headers · Enviado**. El conmutador elegido se mantiene al cambiar de fila, para comparar
  lo mismo de una petición a otra. Al terminar una ejecución con fallos se selecciona el primero y se muestran
  sus pruebas.
- **Enviado** es solo lectura y muestra la verdad: URL final, todos los headers (también los que agrega el
  motor, marcados "se agrega al enviar") y el body ya expandido. Los secretos se enmascaran.

### 17.5 Secretos

Un secreto nunca se muestra en claro sin una acción explícita: el panel de variables, la vista previa de cURL y
el detalle de una ejecución usan `••••`; el editor de entornos los muestra con **Mostrar valores**. Copiar como
cURL copia los valores reales y lo dice en el aviso.
