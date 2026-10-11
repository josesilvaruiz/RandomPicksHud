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
    // Misma fuente de tier/tipo que SharpTimer (sharptimer_remote_data). Vacío = no consultar.
    [JsonPropertyName("MapInfoUrl")] public string MapInfoUrl { get; set; } = "https://raw.githubusercontent.com/Letaryat/poor-SharpTimer/main/remote_data/surf_.json";
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
        _connStr = BuildConnectionString();
        // El manifest quiere el recurso compilado (.vxml); la entidad, el nombre fuente (.xml).
        RegisterListener<Listeners.OnServerPrecacheResources>(m => m.AddResource(Path.ChangeExtension(Config.Layout, ".vxml")));
        RegisterListener<Listeners.OnMapStart>(map =>
        {
            _map = map; _hud = null; _stats.Clear(); _top = Array.Empty<Row>(); _tier = "-"; _type = "";
            FetchMapInfo(map);
            Refresh();
        });
        AddTimer(Config.RefreshSeconds, Refresh, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
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
        if (e.Userid is { IsBot: false } && !_recreatePending)
        {
            _recreatePending = true;
            AddTimer(1.5f, () => { _recreatePending = false; Recreate(); });
        }
        return HookResult.Continue;
    }

    [ConsoleCommand("css_hud", "Toggle the side HUD")]
    public void OnToggle(CCSPlayerController? p, CommandInfo c)
    {
        if (p is null) return;
        var on = !_disabled.Remove(p.Slot);
        if (!on) _disabled.Add(p.Slot);
        p.PrintToChat($" Side HUD: {(on ? "on" : "off")}");
        Push(p);
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
        return true;
    }

    void Recreate()
    {
        var old = _hud;
        _hud = null;
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
        if (string.IsNullOrEmpty(Config.MapInfoUrl)) return;
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

    void Push(CCSPlayerController p)
    {
        if (p is not { IsValid: true, IsBot: false } || _hud is not { IsValid: true }) return;
        var hud = _hud;
        var hidden = _disabled.Contains(p.Slot);
        hud.SetHasClassForPlayer(p, "rp_left", "hidden", hidden);
        hud.SetHasClassForPlayer(p, "rp_right", "hidden", hidden);
        if (hidden) return;

        _stats.TryGetValue(p.SteamID, out var st);
        var top = _top;
        var maxVel = ConVar.Find("sv_maxvelocity")?.GetPrimitiveValue<float>() ?? 3500f;
        void Set(string id, string v) => hud.SetDialogVariableStringForPlayer(p, id, id, v);

        Set("rp_map", _map);
        Set("rp_mode", _type.Length > 0 ? _type.ToUpperInvariant() : Config.Mode.ToUpperInvariant());
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
