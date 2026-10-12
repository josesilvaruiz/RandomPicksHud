<div align="center">

# RandomPicksHud

**A real Panorama HUD for Counter-Strike 2 timer servers — surf, bhop, kz, minigames.**

Map info, personal best, record, rank and top 5 on the sides; speed, timer, strafe sync and keys at the bottom.
Drawn by the game's own UI (`custom_hud_layout`), not by center-text HTML: no flicker, no overlap with chat.

Made by **[Random Picks](https://github.com/josesilvaruiz)** · CounterStrikeSharp plugin · MIT

[English](#english) · [Español](#español)

</div>

---

## English

### Features

| Panel | Shows |
|---|---|
| **Left** | Map name, map type (`SURF`, `BHOP`, `MG`, `KZ`, `DEATHRUN`, `JAIL`… from the map prefix), mode, tier, max velocity, your personal best, the record, your rank and total finishes |
| **Right** | The map's top 5 (name and time) |
| **Bottom** | Speed, the SharpTimer timer, strafe **sync %** while running, and the keys you hold (`A W S D`, jump, duck) |

- Data comes from **SharpTimer**: records from its MySQL database, the live timer and sync straight from the running plugin.
- Every player can switch between this HUD and SharpTimer's own HUD with **`!rphud`**.
- Each panel can be turned off, so the same build works on surf and on minigames servers.
- A small watermark (**`RANDOM PICKS`** by default) sits next to the mode and under the stopped timer. Change it or clear it in the config.

### Requirements

- CS2 dedicated server with [Metamod:Source](https://www.sourcemm.net/) and [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) **1.0.374 or newer** (the `custom_hud_layout` API).
- [SharpTimer](https://github.com/Letaryat/poor-SharpTimer) with MySQL enabled, for records and the top 5. Without it the bottom panel still shows speed and keys.
- [MultiAddonManager](https://github.com/Source2ZE/MultiAddonManager), so every client downloads the Workshop addon that contains the HUD layout.

### Installation

The HUD has two halves: the **layout** (a Workshop addon the clients download) and the **plugin** (on the server).

**1. Publish the layout as a Workshop addon**

The release zip has the compiled layout in `workshop-addon/`. Put it in a Workshop addon and publish it:

1. In Steam, install **Counter-Strike 2 Workshop Tools** (Library → CS2 → DLC).
2. Create an addon, for example `my_hud`, and copy the `panorama` folder from `workshop-addon/` into
   `…\Counter-Strike Global Offensive\game\csgo_addons\my_hud\`.
3. Publish it from the Workshop Tools and note its Workshop ID.

You can also add those files to an addon you already publish, such as your server's content pack.
To change the design, edit the sources in `addon/panorama/` and compile them from `game\bin\win64` of CS2:

```
resourcecompiler.exe -nop4 -i "<CS2>\content\csgo_addons\my_hud\panorama\layout\custom_game\randompicks_hud.xml"
```

**2. Make clients download it**

In `cfg/multiaddonmanager/multiaddonmanager.cfg`, add the Workshop ID to both lists:

```
mm_extra_addons          "<workshop id>"
mm_client_extra_addons   "<workshop id>"
```

**3. Install the plugin**

Copy `addons/` from the release zip into the server's `game/csgo/`. You end up with:

```
addons/counterstrikesharp/plugins/RandomPicksHud/RandomPicksHud.dll
addons/counterstrikesharp/plugins/RandomPicksHud/RandomPicksHud.deps.json
addons/counterstrikesharp/plugins/RandomPicksHud/MySqlConnector.dll
```

Restart the server or change the map. The config is created on first load in
`addons/counterstrikesharp/configs/plugins/RandomPicksHud/RandomPicksHud.json`.
The plugin reads SharpTimer's database settings from `cfg/SharpTimer/mysqlConfig.json`, so there is nothing else to set up.

### Configuration

| Key | Default | What it does |
|---|---|---|
| `Layout` | `panorama/layout/custom_game/randompicks_hud.xml` | Layout path inside the addon. Use the **source** name (`.xml`), not `.vxml_c`. |
| `Mode` | `Standard` | SharpTimer mode used for records and shown on the left. |
| `RefreshSeconds` | `5` | How often records are read and the whole HUD is resent. |
| `HideSharpTimerHudByDefault` | `false` | `true` hides SharpTimer's center HUD on join. Players get it back with `!rphud`. |
| `OnlyForSteamIds` | `[]` | SteamID64 list to test the HUD with a few players. Empty means everyone. |
| `MapInfoUrl` | SharpTimer surf `remote_data` | JSON with tier and type per map. Empty disables it. |
| `ShowSidePanels` | `true` | Left and right panels. `false` also skips every database query. |
| `ShowBottomPanel` | `true` | Speed, timer, sync and keys. |
| `Watermark` | `RANDOM PICKS` | Text after the mode and under the stopped timer. Empty means no watermark. |

A full example is in [`examples/RandomPicksHud.json`](examples/RandomPicksHud.json).
A minigames server usually wants `"MapInfoUrl": ""`, and a server without SharpTimer records wants `"ShowSidePanels": false`.

### Commands

| Command | Who | What |
|---|---|---|
| `!rphud` | Everyone | Switch between this HUD and SharpTimer's HUD |
| `!hudreload` | `@css/root` | Recreate the HUD entity for everybody |

### Troubleshooting

- **Nobody sees the HUD.** Check that the addon is in `mm_client_extra_addons` and that `Layout` matches the file inside it.
  The server log prints `[RandomPicksHud] custom_hud_layout #N layout=…` every time the HUD is created.
- **Only some players see it.** Empty `OnlyForSteamIds`.
- **The bottom panel disappears for a moment when someone joins.** CS2 clears HUD texts for players already in the game when another one joins. The plugin resends everything every `RefreshSeconds`.
- **Never create HUD entities from `OnMapStart`.** This plugin creates the HUD when players connect or spawn. Doing it while the map is still loading crashes the server.

---

## Español

### Qué hace

HUD Panorama de verdad para servidores de CS2 con cronómetro: surf, bhop, kz y minijuegos. La dibuja la propia interfaz del juego (`custom_hud_layout`), no el texto HTML del centro de la pantalla, así que ni parpadea ni tapa el chat.

- **Panel izquierdo:** nombre y tipo del mapa según su prefijo, modo, tier, velocidad máxima, tu mejor tiempo, el récord, tu puesto y las veces que se ha completado.
- **Panel derecho:** el top 5 del mapa.
- **Abajo:** velocidad, cronómetro de SharpTimer, **sync** mientras corres y las teclas pulsadas.
- **Marca de agua:** `RANDOM PICKS` por defecto, junto al modo y bajo el cronómetro parado. Se cambia o se quita con `Watermark`.
- Cada jugador alterna entre esta HUD y la de SharpTimer con **`!rphud`**.

### Instalación

1. **Diseño:** copia la carpeta `panorama` de `workshop-addon/` en un addon de Workshop, dentro de `game\csgo_addons\<addon>\`, y publícalo con las Workshop Tools de CS2. Si ya publicas un pack de contenido para tu servidor, también puedes añadirla a ese pack.
2. **Descarga en los clientes:** añade el ID del addon a `mm_extra_addons` y `mm_client_extra_addons` en `cfg/multiaddonmanager/multiaddonmanager.cfg`.
3. **Plugin:** copia `addons/` del zip en `game/csgo/` del servidor y reinicia o cambia de mapa.
4. **Configuración:** se crea sola en `addons/counterstrikesharp/configs/plugins/RandomPicksHud/RandomPicksHud.json`. Las opciones son las de la tabla de arriba. La conexión a la base de datos la toma de la de SharpTimer.

Requisitos: CounterStrikeSharp 1.0.374 o superior, SharpTimer con MySQL para los récords, y MultiAddonManager.

### Compilar

```
dotnet build -c Release
```

De `bin/Release/net10.0/` se copian a `plugins/RandomPicksHud/` tres ficheros: `RandomPicksHud.dll`, `RandomPicksHud.deps.json` y `MySqlConnector.dll`. El resto ya lo trae CounterStrikeSharp.
