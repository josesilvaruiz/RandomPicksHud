# SurfHud

HUD lateral para el servidor surf de Random Picks (CS2 + CounterStrikeSharp ≥ 1.0.374).

- `addon/panorama/…`: layout y estilos Panorama. Van dentro de un addon de Workshop (`randompicks_hud`),
  compilados con `resourcecompiler.exe` de las Workshop Tools y publicados; los clientes lo descargan por
  `mm_client_extra_addons` (MultiAddonManager).
- `src/SurfHud.cs`: crea una `custom_hud_layout` con ese layout y la rellena por jugador con los datos de
  SharpTimer (MySQL: PB, récord, rank, finishes, top 5) y el tier/tipo del mapa de `remote_data` de SharpTimer.

Compilar el layout (desde `game/bin/win64` de CS2):

```
resourcecompiler.exe -nop4 -i "<CS2>\content\csgo_addons\randompicks_hud\panorama\layout\custom_game\randompicks_surf_hud.xml"
```

Comandos: `!hud` (ocultar/mostrar para ti), `!hudreload` (admin: recrear la entidad).
Config: `configs/plugins/SurfHud/SurfHud.json` (`Layout` debe ser el nombre fuente `.xml`).

Paneles (mismo DLL para surf y minigames):

- `ShowSidePanels` (por defecto `true`): paneles laterales (mapa, tier, PB, récord, rank, finishes y top 5 de SharpTimer).
  Llevan la etiqueta fija «SURF» del layout. Con `false` no se muestran ni se consulta MySQL ni `MapInfoUrl`.
- `ShowBottomPanel` (por defecto `true`): panel inferior con velocidad, cronómetro de SharpTimer (+ sync) y teclas.

Surf: valores por defecto. Minigames: `"ShowSidePanels": false`, `"MapInfoUrl": ""` (solo velocidad, cronómetro de
SharpTimer en los mapas con zonas y teclas).
