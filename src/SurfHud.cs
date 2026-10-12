using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using MySqlConnector;

namespace SurfHud;

public class HudConfig : BasePluginConfig
{
    // Nombre FUENTE (.xml) del layout dentro del addon de Workshop; el cliente rechaza ".vxml" aquí.
    [JsonPropertyName("Layout")] public string Layout { get; set; } = "panorama/layout/custom_game/randompicks_surf_hud.xml";
    [JsonPropertyName("Mode")] public string Mode { get; set; } = "Standard";
    [JsonPropertyName("RefreshSeconds")] public float RefreshSeconds { get; set; } = 5f;
    // Al entrar: true = se oculta la HUD central de SharpTimer y se usa esta; false = se ven las dos hasta que el jugador alterne.
    [JsonPropertyName("HideSharpTimerHudByDefault")] public bool HideSharpTimerHudByDefault { get; set; } = false;
    // Si tiene algo, la HUD solo se muestra a estos SteamID64 (fase de pruebas). Vacío = todos.
    [JsonPropertyName("OnlyForSteamIds")] public List<string> OnlyForSteamIds { get; set; } = new();
    // Misma fuente de tier/tipo que SharpTimer (sharptimer_remote_data). Vacío = no consultar.
    [JsonPropertyName("MapInfoUrl")] public string MapInfoUrl { get; set; } = "https://raw.githubusercontent.com/Letaryat/poor-SharpTimer/main/remote_data/surf_.json";
    // Paneles laterales (mapa/tier/PB/récord y top 5, con la etiqueta fija "SURF"). false = ni se muestran ni se consulta MySQL.
    [JsonPropertyName("ShowSidePanels")] public bool ShowSidePanels { get; set; } = true;
    // Panel inferior: velocidad, cronómetro de SharpTimer y teclas.
    [JsonPropertyName("ShowBottomPanel")] public bool ShowBottomPanel { get; set; } = true;
    // Marca de agua: tras el modo en el panel izquierdo y en lugar de "STOPPED" con el cronómetro parado. Vacío = sin marca.
    [JsonPropertyName("Watermark")] public string Watermark { get; set; } = "RANDOM PICKS";
}

record Row(string Name, int Ticks);
record PlayerStat(int Ticks, int Rank);

/// <summary>
/// HUD lateral de surf: una custom_hud_layout (Panorama, fija en pantalla) cuyo layout viene en el addon de Workshop
/// del servidor y que el plugin rellena por jugador con los datos de SharpTimer (MySQL). Técnica de z4lab SurfTimer.
/// </summary>
[MinimumApiVersion(300)]
public class SurfHud : BasePlugin, IPluginConfig<HudConfig>
{
    public override string ModuleName => "SurfHud";
    public override string ModuleVersion => "1.0.0";
    public HudConfig Config { get; set; } = new();

    CCSCustomHudLayout? _hud;
    bool _recreatePending;
    readonly HashSet<int> _disabled = new();
    readonly HashSet<int> _spawned = new();  // ya aparecieron una vez desde que entraron
    readonly HashSet<int> _stHidden = new(); // jugadores a los que ya se les ocultó la HUD de SharpTimer al entrar
    readonly ConcurrentDictionary<ulong, PlayerStat?> _stats = new();
    volatile IReadOnlyList<Row> _top = Array.Empty<Row>();
    int _finishes, _total;
    string _tier = "-", _type = "";
    string _connStr = "";
    string _map = "";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public void OnConfigParsed(HudConfig config) => Config = config;

    public override void Load(bool hotReload)
    {
        if (Config.ShowSidePanels) _connStr = BuildConnectionString();
        // El manifest quiere el recurso compilado (.vxml); la entidad, el nombre fuente (.xml).
        RegisterListener<Listeners.OnServerPrecacheResources>(m => m.AddResource(Path.ChangeExtension(Config.Layout, ".vxml")));
        RegisterListener<Listeners.OnMapStart>(map =>
        {
            _map = map; _hud = null; _stats.Clear(); _live.Clear(); _top = Array.Empty<Row>(); _tier = "-"; _type = "";
            FetchMapInfo(map);
            Refresh();
        });
        AddTimer(Config.RefreshSeconds, Refresh, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        RegisterListener<Listeners.OnTick>(OnTick);
        _map = Server.MapName;
        if (hotReload) { FetchMapInfo(_map); Refresh(); }
    }

    public override void Unload(bool hotReload)
    {
        if (_hud is { IsValid: true }) _hud.Remove();
        _hud = null;
    }

    // El cliente solo construye la HUD de una entidad que aparece estando él ya en el juego: al entrar alguien
    // se recrea (con retardo, agrupando entradas simultáneas) y se reenvía todo a todos.
    [GameEventHandler(HookMode.Post)]
    public HookResult OnConnectFull(EventPlayerConnectFull e, GameEventInfo i)
    {
        if (e.Userid is { IsBot: false } p && Allowed(p) && !_recreatePending)
        {
            _recreatePending = true;
            AddTimer(1.5f, () => { _recreatePending = false; Recreate(); });
        }
        return HookResult.Continue;
    }

    // Alterna por jugador entre esta HUD y la central de SharpTimer (se ocultan la una a la otra).
    [ConsoleCommand("css_rphud", "Switch between the Random Picks HUD and the SharpTimer HUD")]
    public void OnToggle(CCSPlayerController? p, CommandInfo c)
    {
        if (p is null || !Allowed(p)) return;
        var on = !_disabled.Remove(p.Slot);
        if (!on) _disabled.Add(p.Slot);
        SetSharpTimerHud(p.Slot, visible: !on);
        p.PrintToChat($" HUD: {(on ? "Random Picks" : "SharpTimer")} (type !rphud to switch)");
        Push(p);
    }

    // SharpTimer guarda por jugador si su HUD está oculta (lo mismo que hace su comando !hud).
    void SetSharpTimerHud(int slot, bool visible)
    {
        try
        {
            if (_stTimers is not System.Collections.IDictionary dict || !dict.Contains(slot)) return;
            var t = dict[slot]!;
            t.GetType().GetProperty("HideTimerHud")?.SetValue(t, !visible);
            t.GetType().GetProperty("HideKeys")?.SetValue(t, !visible);
        }
        catch (Exception ex) { Console.WriteLine($"[SurfHud] SharpTimer hud toggle: {ex.Message}"); }
    }

    [GameEventHandler]
    public HookResult OnDisconnect(EventPlayerDisconnect e, GameEventInfo i)
    {
        if (e.Userid is { } p)
        {
            _disabled.Remove(p.Slot); _stHidden.Remove(p.Slot); _spawned.Remove(p.Slot);
            foreach (var k in _live.Keys.Where(k => k.Slot == p.Slot).ToList()) _live.Remove(k);
        }
        return HookResult.Continue;
    }

    // Por si connect_full llega antes de que el cliente esté del todo dentro: también tras su primera aparición.
    // En cada aparición (vuelta de espectador o del AFK) el cliente puede haber perdido la HUD: se le reenvía todo.
    [GameEventHandler(HookMode.Post)]
    public HookResult OnSpawn(EventPlayerSpawn e, GameEventInfo i)
    {
        if (e.Userid is not { IsBot: false } p || !Allowed(p)) return HookResult.Continue;
        if (_spawned.Add(p.Slot) && !_stHidden.Contains(p.Slot) && !_recreatePending)
        {
            _recreatePending = true;
            AddTimer(2f, () => { _recreatePending = false; Recreate(); Console.WriteLine($"[SurfHud] recreated after spawn of {p.PlayerName}"); });
            return HookResult.Continue;
        }
        var slot = p.Slot;
        AddTimer(0.5f, () =>
        {
            foreach (var k in _live.Keys.Where(k => k.Slot == slot).ToList()) _live.Remove(k);
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } again) Push(again);
        });
        return HookResult.Continue;
    }

    [ConsoleCommand("css_hudreload", "Recreate the side HUD entity")]
    [RequiresPermissions("@css/root")]
    public void OnReload(CCSPlayerController? p, CommandInfo c) { Recreate(); c.ReplyToCommand("HUD recreated"); }

    bool EnsureHud()
    {
        if (_hud is { IsValid: true }) return true;
        foreach (var stale in Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout"))
            if (stale.IsValid && stale.StrLayout == Config.Layout) stale.Remove();
        var hud = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
        if (hud is null || !hud.IsValid) { Console.WriteLine("[SurfHud] could not create custom_hud_layout"); return false; }
        var kv = new CEntityKeyValues();
        kv.SetString("layout", Config.Layout);
        hud.StrLayout = Config.Layout;
        hud.DispatchSpawn(kv);
        _hud = hud;
        Server.NextFrame(() =>
        {
            if (_hud is { IsValid: true })
                Console.WriteLine($"[SurfHud] custom_hud_layout #{_hud.Index} layout='{_hud.StrLayout}' panels={_hud.PanelIds.Count} vars={_hud.DialogVariableNames.Count} states={_hud.PlayerLayoutStates.Count}");
            else Console.WriteLine("[SurfHud] custom_hud_layout became invalid right after spawn");
        });
        return true;
    }

    void Recreate()
    {
        var old = _hud;
        _hud = null;
        _live.Clear(); // entidad nueva: el cliente parte de cero, hay que reenviarlo todo
        if (!EnsureHud()) return;
        if (old is { IsValid: true }) old.Remove();
        PushAll();
    }

    static string Fmt(int ticks)
    {
        var t = TimeSpan.FromSeconds(ticks / 64.0);
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    void FetchMapInfo(string map)
    {
        if (!Config.ShowSidePanels || string.IsNullOrEmpty(Config.MapInfoUrl)) return;
        Task.Run(async () =>
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(Config.MapInfoUrl));
                if (!doc.RootElement.TryGetProperty(map, out var info)) return;
                var tier = info.TryGetProperty("Tier", out var t) ? t.ToString() : "-";
                var type = info.TryGetProperty("Type", out var ty) ? ty.ToString() : "";
                Server.NextFrame(() => { if (_map == map) { _tier = tier; _type = type; PushAll(); } });
            }
            catch (Exception ex) { Console.WriteLine($"[SurfHud] map info: {ex.Message}"); }
        });
    }

    void Refresh()
    {
        // Como los laterales: el panel de abajo se reenvía entero cada RefreshSeconds. CS2 borra los textos de la HUD a
        // quien ya estaba dentro cuando entra otro jugador o al volver de espectador, y con la caché no se reenviaban.
        _live.Clear();
        // Sin paneles laterales no hay nada que refrescar. Tampoco se crea la entidad aquí: desde OnMapStart, con el mapa
        // a medio cargar, tumbaba el servidor (segfault en minigames). La crean la entrada/aparición de los jugadores.
        if (!Config.ShowSidePanels) return;
        var map = _map;
        var mode = Config.Mode;
        var players = Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, SteamID: > 0 }).Select(p => p.SteamID).ToList();
        Task.Run(async () =>
        {
            try
            {
                await using var db = new MySqlConnection(_connStr);
                await db.OpenAsync();
                const string where = "MapName=@m AND Style=0 AND (Mode=@mo OR Mode='')";
                var top = new List<Row>();
                await using (var cmd = new MySqlCommand($"SELECT PlayerName,TimerTicks FROM PlayerRecords WHERE {where} ORDER BY TimerTicks LIMIT 5", db))
                {
                    cmd.Parameters.AddWithValue("@m", map); cmd.Parameters.AddWithValue("@mo", mode);
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync()) top.Add(new Row(r.IsDBNull(0) ? "?" : r.GetString(0), r.GetInt32(1)));
                }
                int finishes, total;
                await using (var cmd = new MySqlCommand($"SELECT COALESCE(SUM(TimesFinished),0), COUNT(*) FROM PlayerRecords WHERE {where}", db))
                {
                    cmd.Parameters.AddWithValue("@m", map); cmd.Parameters.AddWithValue("@mo", mode);
                    await using var r = await cmd.ExecuteReaderAsync();
                    await r.ReadAsync();
                    finishes = Convert.ToInt32(r.GetValue(0)); total = Convert.ToInt32(r.GetValue(1));
                }
                foreach (var sid in players)
                {
                    PlayerStat? st = null;
                    await using var cmd = new MySqlCommand(
                        $"SELECT TimerTicks,(SELECT COUNT(*)+1 FROM PlayerRecords x WHERE x.MapName=@m AND x.Style=0 AND (x.Mode=@mo OR x.Mode='') AND x.TimerTicks<p.TimerTicks) " +
                        $"FROM PlayerRecords p WHERE {where} AND SteamID=@s ORDER BY TimerTicks LIMIT 1", db);
                    cmd.Parameters.AddWithValue("@m", map); cmd.Parameters.AddWithValue("@s", sid.ToString()); cmd.Parameters.AddWithValue("@mo", mode);
                    await using var r = await cmd.ExecuteReaderAsync();
                    if (await r.ReadAsync()) st = new PlayerStat(r.GetInt32(0), Convert.ToInt32(r.GetValue(1)));
                    _stats[sid] = st;
                }
                _top = top; _finishes = finishes; _total = total;
                Server.NextFrame(PushAll);
            }
            catch (Exception ex) { Console.WriteLine($"[SurfHud] database: {ex.Message}"); }
        });
    }

    void PushAll()
    {
        if (!EnsureHud()) return;
        foreach (var p in Utilities.GetPlayers()) Push(p);
    }

    bool Allowed(CCSPlayerController p) => Config.OnlyForSteamIds.Count == 0 || Config.OnlyForSteamIds.Contains(p.SteamID.ToString());

    void Push(CCSPlayerController p)
    {
        if (p is not { IsValid: true, IsBot: false } || _hud is not { IsValid: true } || !Allowed(p)) return;
        var hud = _hud;
        var hidden = _disabled.Contains(p.Slot) || !Config.ShowSidePanels;
        hud.SetHasClassForPlayer(p, "rp_left", "hidden", hidden);
        hud.SetHasClassForPlayer(p, "rp_right", "hidden", hidden);
        if (hidden) return;

        _stats.TryGetValue(p.SteamID, out var st);
        var top = _top;
        var maxVel = ConVar.Find("sv_maxvelocity")?.GetPrimitiveValue<float>() ?? 3500f;
        void Set(string id, string v) => hud.SetDialogVariableStringForPlayer(p, id, id, v);

        Set("rp_map", _map);
        var mode = _type.Length > 0 ? _type.ToUpperInvariant() : Config.Mode.ToUpperInvariant();
        Set("rp_mode", Config.Watermark.Length > 0 ? $"{mode}  ·  {Config.Watermark}" : mode);
        Set("rp_tier", _tier);
        Set("rp_maxvel", $"{maxVel:0}");
        Set("rp_pb", st is null ? "-" : Fmt(st.Ticks));
        Set("rp_wr", top.Count > 0 ? Fmt(top[0].Ticks) : "-");
        Set("rp_rank", st is null ? "-" : $"{st.Rank}/{_total}");
        Set("rp_fin", _finishes.ToString());
        for (var i = 0; i < 5; i++)
        {
            var has = i < top.Count;
            hud.SetHasClassForPlayer(p, $"rp_top_{i}", "hidden", !has);
            if (!has) continue;
            Set($"rp_top_{i}_name", top[i].Name.Length > 18 ? top[i].Name[..18] : top[i].Name);
            Set($"rp_top_{i}_time", Fmt(top[i].Ticks));
        }
    }

    // ---- Parte de abajo: velocidad, cronómetro y teclas, 32 veces por segundo y solo lo que cambia ----

    readonly Dictionary<(int Slot, string Id), string> _live = new();
    object? _stTimers;          // SharpTimer.Instance.playerTimers (Dictionary<int, PlayerTimerInfo>), por reflexión
    bool _stLookedUp;

    // El estado del cronómetro vive dentro de SharpTimer; se lee por reflexión para no depender de su DLL.
    // Si cambia su estructura, el cronómetro se muestra parado y el resto de la HUD sigue funcionando.
    (bool Running, int Ticks, float Sync)? TimerOf(int slot)
    {
        try
        {
            if (!_stLookedUp)
            {
                _stLookedUp = true;
                const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "SharpTimer");
                var type = asm?.GetTypes().FirstOrDefault(t => t.Name == "SharpTimer" && t.GetMember("playerTimers", Any).Length > 0);
                var instM = type?.GetProperty("Instance", Any) as System.Reflection.MemberInfo ?? type?.GetField("Instance", Any);
                var inst = instM is System.Reflection.PropertyInfo pi ? pi.GetValue(null) : (instM as System.Reflection.FieldInfo)?.GetValue(null);
                var ptM = inst?.GetType().GetField("playerTimers", Any) as System.Reflection.MemberInfo ?? inst?.GetType().GetProperty("playerTimers", Any);
                _stTimers = ptM is System.Reflection.FieldInfo fi ? fi.GetValue(inst) : (ptM as System.Reflection.PropertyInfo)?.GetValue(inst);
                Console.WriteLine($"[SurfHud] SharpTimer timers {(_stTimers is null ? "NOT found" : "found")} (asm={(asm is null ? "no" : "yes")} type={(type is null ? "no" : type.FullName)} instance={(inst is null ? "no" : "yes")})");
            }
            if (_stTimers is not System.Collections.IDictionary dict || !dict.Contains(slot)) return null;
            var t = dict[slot]!;
            var ty = t.GetType();
            var bonus = (bool)(ty.GetProperty("IsBonusTimerRunning")?.GetValue(t) ?? false);
            var running = bonus || (bool)(ty.GetProperty("IsTimerRunning")?.GetValue(t) ?? false);
            var ticks = (int)(ty.GetProperty(bonus ? "BonusTimerTicks" : "TimerTicks")?.GetValue(t) ?? 0);
            var sync = Convert.ToSingle(ty.GetProperty("Sync")?.GetValue(t) ?? ty.GetField("Sync")?.GetValue(t) ?? 0f);
            return (running, ticks, sync);
        }
        catch { return null; }
    }

    void OnTick()
    {
        if (!Config.ShowBottomPanel || Server.TickCount % 2 != 0 || _hud is not { IsValid: true }) return;
        var hud = _hud;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p is not { IsValid: true, IsBot: false, PawnIsAlive: true } || _disabled.Contains(p.Slot) || !Allowed(p)) continue;
            var pawn = p.PlayerPawn.Value;
            if (pawn is null) continue;
            var v = pawn.AbsVelocity;
            var speed = Math.Round(Math.Sqrt(v.X * v.X + v.Y * v.Y));
            var t = TimerOf(p.Slot);
            var b = p.Buttons;
            var keys = $"{(b.HasFlag(PlayerButtons.Moveleft) ? "A" : "_")} {(b.HasFlag(PlayerButtons.Forward) ? "W" : "_")} " +
                       $"{(b.HasFlag(PlayerButtons.Back) ? "S" : "_")} {(b.HasFlag(PlayerButtons.Moveright) ? "D" : "_")} " +
                       $"{(b.HasFlag(PlayerButtons.Jump) ? "J" : "_")} {(b.HasFlag(PlayerButtons.Duck) ? "C" : "_")}";
            Live(hud, p, "rp_speed", $"{speed:0}");
            Live(hud, p, "rp_time", t is { Running: true } ? Fmt(t.Value.Ticks) : "00:00.000");
            // El sync va en la etiqueta de estado: el layout publicado no tiene otra y así no hay que republicar el pack.
            Live(hud, p, "rp_timestate", t is { Running: true } ? $"SYNC {t.Value.Sync:0.0}%" : Config.Watermark.Length > 0 ? Config.Watermark : "STOPPED");
            Live(hud, p, "rp_keys", keys);
            if (!_live.ContainsKey((p.Slot, "rp_bottom")))
            {
                hud.SetHasClassForPlayer(p, "rp_bottom", "hidden", false);
                _live[(p.Slot, "rp_bottom")] = "on";
                if (Config.HideSharpTimerHudByDefault && !_stHidden.Contains(p.Slot)) { _stHidden.Add(p.Slot); SetSharpTimerHud(p.Slot, visible: false); }
            }
        }
    }

    void Live(CCSCustomHudLayout hud, CCSPlayerController p, string id, string value)
    {
        var k = (p.Slot, id);
        if (_live.TryGetValue(k, out var old) && old == value) return;
        _live[k] = value;
        hud.SetDialogVariableStringForPlayer(p, id, id, value);
    }

    string BuildConnectionString()
    {
        var path = Path.Combine(Server.GameDirectory, "csgo", "cfg", "SharpTimer", "mysqlConfig.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        return new MySqlConnectionStringBuilder
        {
            Server = r.GetProperty("Host").GetString(),
            Port = (uint)r.GetProperty("Port").GetInt32(),
            Database = r.GetProperty("Database").GetString(),
            UserID = r.GetProperty("Username").GetString(),
            Password = r.GetProperty("Password").GetString(),
            SslMode = MySqlSslMode.None,
        }.ConnectionString;
    }
}
