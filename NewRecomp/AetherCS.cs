using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace AetherCS;

public class AetherCS : BasePlugin
{
    public override string ModuleName => "AetherCS";
    public override string ModuleVersion => "b5";
    public override string ModuleAuthor => "aesth";

    private static readonly Random _rouletteRng = new Random();
    private static readonly Random _random = new Random();

    private static readonly HttpClient _workshopHttpClient = new HttpClient();
    private readonly HttpClient _httpClient = new HttpClient();

    // Config values (loaded from DiscordConfig.json)
    private string _webhookUrl = "";
    private string _steamApiKey = "";

    // Tactical pause state
    private bool _isTacticalPaused;
    private readonly Dictionary<int, Vector> _pausedPositions = new Dictionary<int, Vector>();

    // Match stat tracking
    private readonly Dictionary<ulong, int> _shotsFired = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, int> _shotsHit = new Dictionary<ulong, int>();
    private readonly Dictionary<ulong, float> _teamBlindTime = new Dictionary<ulong, float>();
    private readonly Dictionary<ulong, Dictionary<ulong, DamageRecord>> _damageMatrix = new Dictionary<ulong, Dictionary<ulong, DamageRecord>>();
    private readonly Dictionary<ulong, string> _playerNames = new Dictionary<ulong, string>();

    // Toggle state
    private readonly HashSet<ulong> _activeHudPlayers = new HashSet<ulong>();
    private readonly HashSet<int> _godModePlayers = new HashSet<int>();
    private readonly HashSet<int> _frozenPlayers = new HashSet<int>();

    public override void Load(bool hotReload)
    {
        LoadConfig();

        RegisterEventHandler<EventRoundStart>(OnRoundStart, HookMode.Pre);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Pre);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire, HookMode.Pre);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt, HookMode.Pre);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind, HookMode.Pre);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Pre);
        RegisterListener<Listeners.OnTick>(OnTick);
    }

    private void LoadConfig()
    {
        string configPath = Path.Combine(ModuleDirectory, "DiscordConfig.json");
        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("WebhookUrl", out JsonElement webhookElement))
            {
                _webhookUrl = webhookElement.GetString() ?? "";
            }
            if (doc.RootElement.TryGetProperty("SteamApiKey", out JsonElement apiKeyElement))
            {
                _steamApiKey = apiKeyElement.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Aether^] Failed to read config: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------
    // Admin commands
    // ---------------------------------------------------------------

    [ConsoleCommand("css_blind", "Instantly flashbang a target player's screen")]
    [RequiresPermissions("@css/generic")]
    public void OnBlindCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount < 2)
        {
            player?.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !blind <player_name>");
            return;
        }

        var target = FindPlayerByName(info.GetArg(1));
        if (target == null || !target.PawnIsAlive || target.PlayerPawn.Value == null)
        {
            player?.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Target player not found or is already dead.");
            return;
        }

        var pawn = target.PlayerPawn.Value;
        pawn.FlashMaxAlpha = 255f;
        pawn.FlashDuration = 5f;
        pawn.BlindStartTime = Server.CurrentTime;
        pawn.BlindUntilTime = Server.CurrentTime + 5f;
        target.ExecuteClientCommand("play sounds/weapons/flashbang/flashbang_explode1.vsnd");

        player?.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Green} You successfully blinded {target.PlayerName}.");
        target.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} You were blinded by an admin!");
    }

    [ConsoleCommand("css_bring", "Bring a target player to your exact position")]
    [RequiresPermissions("@css/generic")]
    public void OnBringCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value == null)
        {
            return;
        }
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !bring <player_name>");
            return;
        }

        var target = FindPlayerByName(info.GetArg(1));
        if (target == null || !target.PawnIsAlive || target.PlayerPawn.Value == null)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Target player not found or dead.");
            return;
        }

        target.PlayerPawn.Value.Teleport(player.PlayerPawn.Value.AbsOrigin, player.PlayerPawn.Value.AbsRotation, new Vector(0f, 0f, 0f));
        player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Green} Summoned {target.PlayerName} to your location.");
    }

    [ConsoleCommand("css_disarm", "Strip all weapons from a specific player")]
    [RequiresPermissions("@css/generic")]
    public void OnDisarmPlayer(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
        {
            return;
        }
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !disarm <player_name>");
            return;
        }

        var target = FindPlayerByName(info.GetArg(1));
        if (target == null || !target.PawnIsAlive || target.PlayerPawn.Value == null)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Target player not found or dead.");
            return;
        }

        StripWeapons(target.PlayerPawn.Value);
        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^] {ChatColors.White}{target.PlayerName} has been completely {ChatColors.Red}disarmed.");
    }

    [ConsoleCommand("css_freeze", "Freeze a specific player completely")]
    [RequiresPermissions("@css/generic")]
    public void OnFreezeCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
        {
            return;
        }
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !freeze <player_name>");
            return;
        }

        var target = FindPlayerByName(info.GetArg(1));
        if (target == null || !target.IsValid)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Target player not found.");
            return;
        }

        int slot = target.Slot;
        if (_frozenPlayers.Contains(slot))
        {
            _frozenPlayers.Remove(slot);
            if (target.PlayerPawn.Value != null)
            {
                target.PlayerPawn.Value.MoveType = MoveType_t.MOVETYPE_WALK;
            }
            Server.PrintToChatAll($" {ChatColors.Purple}[Aether^] {ChatColors.White}{target.PlayerName} has been {ChatColors.Green}unfrozen.");
        }
        else
        {
            _frozenPlayers.Add(slot);
            Server.PrintToChatAll($" {ChatColors.Purple}[Aether^] {ChatColors.White}{target.PlayerName} has been {ChatColors.Red}frozen in place.");
        }
    }

    [ConsoleCommand("css_god", "Toggle absolute invulnerability")]
    [RequiresPermissions("@css/generic")]
    public void OnToggleGodMode(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value == null)
        {
            return;
        }

        int slot = player.Slot;
        if (_godModePlayers.Contains(slot))
        {
            _godModePlayers.Remove(slot);
            player.PlayerPawn.Value.TakesDamage = true;
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.White} God mode {ChatColors.Red}Disabled.");
        }
        else
        {
            _godModePlayers.Add(slot);
            player.PlayerPawn.Value.TakesDamage = false;
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.White} God mode {ChatColors.Green}Enabled.");
        }
    }

    [ConsoleCommand("css_goto", "Teleport directly to a target player's location")]
    [RequiresPermissions("@css/generic")]
    public void OnGotoCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value == null)
        {
            return;
        }
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !goto <player_name>");
            return;
        }

        var target = FindPlayerByName(info.GetArg(1));
        if (target == null || !target.PawnIsAlive || target.PlayerPawn.Value == null)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Target player not found or dead.");
            return;
        }

        player.PlayerPawn.Value.Teleport(target.PlayerPawn.Value.AbsOrigin, target.PlayerPawn.Value.AbsRotation, new Vector(0f, 0f, 0f));
        player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Green} Teleported to {target.PlayerName}.");
    }

    [ConsoleCommand("css_kick", "Instantly sever connection for a target asset")]
    [RequiresPermissions("@css/generic")]
    public void OnKickCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
        {
            return;
        }
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !kick <player_name>");
            return;
        }

        var target = FindPlayerByName(info.GetArg(1));
        if (target == null || !target.IsValid || target.IsBot)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Non-bot player not found.");
            return;
        }

        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^] {ChatColors.Red}{target.PlayerName} {ChatColors.White}was removed from server by admin.");
        Server.ExecuteCommand($"kickid {target.UserId}");
    }

    [ConsoleCommand("css_noclip", "Toggle local player flight and wall-occlusion bypass")]
    [RequiresPermissions("@css/generic")]
    public void OnNoclipCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value == null)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP)
        {
            pawn.MoveType = MoveType_t.MOVETYPE_WALK;
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.White} Noclip flight mode {ChatColors.Red}Disabled.");
        }
        else
        {
            pawn.MoveType = MoveType_t.MOVETYPE_NOCLIP;
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.White} Noclip flight mode {ChatColors.Green}Enabled.");
        }
    }

    [ConsoleCommand("css_roulette", "Spin the wheel for a random round modifier")]
    [RequiresPermissions("@css/generic")]
    public void OnRouletteCommand(CCSPlayerController? player, CommandInfo info)
    {
        int roll = _rouletteRng.Next(0, 4);
        string modifierName = "";

        Server.ExecuteCommand("sv_gravity 800; mp_free_armor 1; mp_respawn_immunitytime 2");

        switch (roll)
        {
            case 0:
                modifierName = "KNIFE ONLY";
                Server.ExecuteCommand("mp_items_prohibited 12345");
                break;
            case 1:
                modifierName = "LOW GRAVITY";
                Server.ExecuteCommand("sv_gravity 200");
                break;
            case 2:
                modifierName = "FLASHBANG SPAM";
                Server.ExecuteCommand("sv_infinite_ammo 2; give weapon_flashbang");
                break;
            case 3:
                modifierName = "SCOUT ONLY";
                Server.ExecuteCommand("mp_items_prohibited 12345; give weapon_ssg08");
                break;
        }

        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.White} Roulette result: {ChatColors.Red}{modifierName}!");
        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.White} Next round is modified. Good luck.");
    }

    [ConsoleCommand("css_rr", "Restart the match instantly")]
    [RequiresPermissions("@css/generic")]
    public void OnRestartRound(CCSPlayerController? player, CommandInfo info)
    {
        Server.ExecuteCommand("mp_restartgame 1");
        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Match restart forced by admin.");
    }

    [ConsoleCommand("css_scout", "Toggle Flying Scoutsman mode")]
    [RequiresPermissions("@css/generic")]
    public void OnScoutModeCommand(CCSPlayerController? player, CommandInfo info)
    {
        Server.ExecuteCommand("sv_gravity 250");
        Server.ExecuteCommand("sv_airaccelerate 100");

        foreach (var p in Utilities.GetPlayers())
        {
            if (p != null && p.IsValid && p.PawnIsAlive)
            {
                p.ExecuteClientCommand("drop");
                p.GiveNamedItem("weapon_ssg08");
                p.GiveNamedItem("weapon_knife");
            }
        }

        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.Green} Flying Scoutsman mode active!");
    }

    [ConsoleCommand("css_slap", "Slap a player, dealing minor damage and throwing their momentum")]
    [RequiresPermissions("@css/generic")]
    public void OnSlapCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
        {
            return;
        }
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !slap <player_name>");
            return;
        }

        var target = FindPlayerByName(info.GetArg(1));
        if (target == null || !target.PawnIsAlive || target.PlayerPawn.Value == null)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Target player not found or dead.");
            return;
        }

        var pawn = target.PlayerPawn.Value;
        pawn.Velocity.X += _random.Next(-300, 300);
        pawn.Velocity.Y += _random.Next(-300, 300);
        pawn.Velocity.Z += _random.Next(150, 300);
        pawn.Health -= 5;

        if (pawn.Health <= 0)
        {
            pawn.CommitSuicide(false, true);
        }

        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^] {ChatColors.White}Admin slapped {ChatColors.Red}{target.PlayerName}.");
    }

    [ConsoleCommand("css_slay", "Instantly eliminate a target player")]
    [RequiresPermissions("@css/generic")]
    public void OnSlayCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
        {
            return;
        }
        if (info.ArgCount < 2)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !slay <player_name>");
            return;
        }

        var target = FindPlayerByName(info.GetArg(1));
        if (target == null || !target.PawnIsAlive || target.PlayerPawn.Value == null)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Target player not found or already dead.");
            return;
        }

        target.PlayerPawn.Value.CommitSuicide(false, true);
        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^] {ChatColors.White}Admin executed lethal {ChatColors.Red}slay {ChatColors.White}on {target.PlayerName}.");
    }

    [ConsoleCommand("css_pause", "Indefinitely pause match countdown and lock movement")]
    [RequiresPermissions("@css/generic")]
    public void OnTacPauseCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (_isTacticalPaused)
        {
            player?.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Match is already paused. Use !unpause to resume.");
            return;
        }

        _isTacticalPaused = true;
        _pausedPositions.Clear();

        foreach (var p in Utilities.GetPlayers())
        {
            if (p != null && p.IsValid && p.PawnIsAlive && p.PlayerPawn.Value != null)
            {
                _pausedPositions[p.Slot] = p.PlayerPawn.Value.AbsOrigin ?? new Vector(0f, 0f, 0f);
            }
        }

        Server.ExecuteCommand("mp_pause_match");
        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Match has been tactically paused by admin. Positions locked.");
    }

    [ConsoleCommand("css_unpause", "Resume match countdown and restore movement frames")]
    [RequiresPermissions("@css/generic")]
    public void OnTacUnpauseCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!_isTacticalPaused)
        {
            player?.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Match is not paused.");
            return;
        }

        _isTacticalPaused = false;
        _pausedPositions.Clear();
        Server.ExecuteCommand("mp_unpause_match");
        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.Green} Match resumed! Fight.");
    }

    private void EnforceTacticalPauseMovement()
    {
        if (!_isTacticalPaused)
        {
            return;
        }

        foreach (var p in Utilities.GetPlayers())
        {
            if (p != null && p.IsValid && p.PawnIsAlive && p.PlayerPawn.Value != null
                && _pausedPositions.TryGetValue(p.Slot, out Vector lockedPosition))
            {
                p.PlayerPawn.Value.Teleport(lockedPosition, p.PlayerPawn.Value.AbsRotation, new Vector(0f, 0f, 0f));
            }
        }
    }

    [ConsoleCommand("css_team", "Force swap a player's team alliance assignment")]
    [RequiresPermissions("@css/generic")]
    public void OnTeamCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
        {
            return;
        }
        if (info.ArgCount < 3)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !team <player_name> <t/ct/spec>");
            return;
        }

        var target = FindPlayerByName(info.GetArg(1));
        string teamArg = info.GetArg(2).ToLower();

        if (target == null || !target.IsValid)
        {
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Target profile context not found.");
            return;
        }

        CsTeam team = CsTeam.Spectator;
        if (teamArg is "t" or "terrorist")
        {
            team = CsTeam.Terrorist;
        }
        else if (teamArg is "ct" or "counter")
        {
            team = CsTeam.CounterTerrorist;
        }

        target.ChangeTeam(team);
        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^] {ChatColors.White}Forced team change on {target.PlayerName} to team code {(int)team}.");
    }

    // ---------------------------------------------------------------
    // Workshop map search
    // ---------------------------------------------------------------

    [ConsoleCommand("css_searchmap", "Search the Steam Workshop for CS2 maps matching a query")]
    [RequiresPermissions("@css/generic")]
    public void OnSearchWorkshopMapCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (info.ArgCount < 2)
        {
            player?.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Grey} Usage: !searchmap <search_term>");
            return;
        }

        string query = info.ArgString.Trim();
        Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.White} Querying Steam Workshop services for '{ChatColors.Green}{query}{ChatColors.White}'...");
        _ = ExecuteWorkshopSearch(query);
    }

    private async Task ExecuteWorkshopSearch(string query)
    {
        const string apiUrl = "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/";

        try
        {
            var queryParams = new Dictionary<string, string>
            {
                ["key"] = _steamApiKey,
                ["query_type"] = "0",
                ["page"] = "1",
                ["numperpage"] = "5",
                ["creator_appid"] = "730",
                ["appid"] = "730",
                ["search_text"] = query,
                ["requiredtags[0]"] = "Map",
                ["return_details"] = "1"
            };

            string encodedParams = await new FormUrlEncodedContent(queryParams).ReadAsStringAsync();
            HttpResponseMessage response = await _workshopHttpClient.GetAsync(apiUrl + "?" + encodedParams);

            if (!response.IsSuccessStatusCode)
            {
                Server.NextFrame(() =>
                {
                    Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Steam Web Services returned an error code: {response.StatusCode}");
                });
                return;
            }

            string json = await response.Content.ReadAsStringAsync();
            SteamWorkshopRoot? searchResult = JsonSerializer.Deserialize<SteamWorkshopRoot>(json);

            if (searchResult?.Response?.Files == null || searchResult.Response.Files.Count == 0)
            {
                Server.NextFrame(() =>
                {
                    Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.Red} No matching workshop maps discovered. Try another query.");
                });
                return;
            }

            Server.NextFrame(() =>
            {
                Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.Green} Top Matching Workshop Map Items:");
                foreach (var file in searchResult.Response.Files)
                {
                    string title = file.Title.Length > 30 ? file.Title.Substring(0, 27) + "..." : file.Title;
                    Server.PrintToChatAll($" {ChatColors.Purple}» {ChatColors.White}{title} | {ChatColors.LightPurple}ID: {ChatColors.Green}{file.PublishedFileId}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Aether^] Workshop search task exception encountered: " + ex.Message);
            Server.NextFrame(() =>
            {
                Server.PrintToChatAll($" {ChatColors.Purple}[Aether^]{ChatColors.Red} Engine network failure processing the search lookup.");
            });
        }
    }

    // ---------------------------------------------------------------
    // Weapon spawning
    // ---------------------------------------------------------------

    [ConsoleCommand("css_ak", "Spawn an AK-47 instantly")]
    public void OnSpawnAK(CCSPlayerController? player, CommandInfo info) => GivePrimaryWeapon(player, "weapon_ak47", "AK-47");

    [ConsoleCommand("css_m4", "Spawn an M4A4 instantly")]
    public void OnSpawnM4(CCSPlayerController? player, CommandInfo info) => GivePrimaryWeapon(player, "weapon_m4a1", "M4A4");

    [ConsoleCommand("css_m4s", "Spawn an M4A1-S instantly")]
    public void OnSpawnM4S(CCSPlayerController? player, CommandInfo info) => GivePrimaryWeapon(player, "weapon_m4a1_silencer", "M4A1-S");

    private void GivePrimaryWeapon(CCSPlayerController? player, string weaponClassName, string weaponDisplayName)
    {
        if (player == null || !player.IsValid || !player.PawnIsAlive || player.PlayerPawn.Value == null)
        {
            return;
        }

        StripWeapons(player.PlayerPawn.Value, primaryOnly: true);

        Server.NextFrame(() =>
        {
            if (player.IsValid && player.PawnIsAlive)
            {
                player.GiveNamedItem(weaponClassName);
                player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Green} Spawned {weaponDisplayName}.");
            }
        });
    }

    private void StripWeapons(CCSPlayerPawn pawn, bool primaryOnly = false)
    {
        if (pawn.WeaponServices?.MyWeapons == null)
        {
            return;
        }

        foreach (var weaponHandle in pawn.WeaponServices.MyWeapons.ToList())
        {
            var weapon = weaponHandle.Value;
            if (weapon == null || !weapon.IsValid || string.IsNullOrEmpty(weapon.DesignerName))
            {
                continue;
            }
            if (primaryOnly && !IsPrimaryWeapon(weapon.DesignerName))
            {
                continue;
            }

            pawn.Controller.Value?.As<CCSPlayerController>()?.ExecuteClientCommand("drop " + weapon.DesignerName);

            var tempWeapon = weapon;
            Server.NextFrame(() =>
            {
                if (tempWeapon.IsValid)
                {
                    tempWeapon.Remove();
                }
            });
        }
    }

    private bool IsPrimaryWeapon(string className)
    {
        string name = className.ToLower();
        return name.Contains("ak47") || name.Contains("m4a1") || name.Contains("awp")
            || name.Contains("galilar") || name.Contains("famas") || name.Contains("ssg08")
            || name.Contains("aug") || name.Contains("sg556");
    }

    // ---------------------------------------------------------------
    // HUD / tick logic
    // ---------------------------------------------------------------

    private void OnSpeedCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid)
        {
            return;
        }

        if (_activeHudPlayers.Contains(player.SteamID))
        {
            _activeHudPlayers.Remove(player.SteamID);
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.White} Velocity HUD Disabled.");
        }
        else
        {
            _activeHudPlayers.Add(player.SteamID);
            player.PrintToChat($" {ChatColors.Purple}[Aether^]{ChatColors.Green} Velocity HUD Enabled.");
        }
    }

    private void OnTick()
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || p.IsBot || !p.PawnIsAlive || p.PlayerPawn.Value == null)
            {
                continue;
            }

            if (_activeHudPlayers.Contains(p.SteamID))
            {
                Vector velocity = p.PlayerPawn.Value.AbsVelocity;
                double speed = Math.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
                p.PrintToCenterHtml($"<font color='#b08dff'>Speed: {Math.Round(speed)} u/s</font>");
            }

            if (_frozenPlayers.Contains(p.Slot))
            {
                p.PlayerPawn.Value.Velocity.X = 0f;
                p.PlayerPawn.Value.Velocity.Y = 0f;
                p.PlayerPawn.Value.Velocity.Z = 0f;
                p.PlayerPawn.Value.MoveType = MoveType_t.MOVETYPE_NONE;
            }
        }

        EnforceTacticalPauseMovement();
    }

    // ---------------------------------------------------------------
    // Event handlers / stat tracking
    // ---------------------------------------------------------------

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _shotsFired.Clear();
        _shotsHit.Clear();
        _teamBlindTime.Clear();
        _damageMatrix.Clear();
        _playerNames.Clear();
        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var attacker = @event.Attacker;
        var victim = @event.Userid;

        if (attacker == null || !attacker.IsValid || victim == null || !victim.IsValid)
        {
            return HookResult.Continue;
        }

        ulong attackerSlot = (ulong)attacker.Slot;
        ulong victimSlot = (ulong)victim.Slot;

        _playerNames[attackerSlot] = attacker.PlayerName;
        _playerNames[victimSlot] = victim.PlayerName;

        if (!attacker.IsBot)
        {
            _shotsHit.TryAdd(attacker.SteamID, 0);
            _shotsHit[attacker.SteamID]++;
        }

        if (attackerSlot != victimSlot && attacker.TeamNum != victim.TeamNum)
        {
            if (!_damageMatrix.ContainsKey(attackerSlot))
            {
                _damageMatrix[attackerSlot] = new Dictionary<ulong, DamageRecord>();
            }
            if (!_damageMatrix[attackerSlot].ContainsKey(victimSlot))
            {
                _damageMatrix[attackerSlot][victimSlot] = new DamageRecord();
            }

            _damageMatrix[attackerSlot][victimSlot].Damage += @event.DmgHealth;
            _damageMatrix[attackerSlot][victimSlot].Hits++;
        }

        return HookResult.Continue;
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var shooter = @event.Userid;
        if (shooter == null || !shooter.IsValid || shooter.IsBot)
        {
            return HookResult.Continue;
        }

        _shotsFired.TryAdd(shooter.SteamID, 0);
        _shotsFired[shooter.SteamID]++;

        return HookResult.Continue;
    }

    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (victim == null || attacker == null)
        {
            return HookResult.Continue;
        }

        if (victim.TeamNum == attacker.TeamNum && victim.SteamID != attacker.SteamID)
        {
            _teamBlindTime.TryAdd(attacker.SteamID, 0f);
            _teamBlindTime[attacker.SteamID] += @event.BlindDuration;
        }

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (p.IsValid && !p.IsBot)
            {
                PrintDamageReport(p);
            }
        }
        return HookResult.Continue;
    }

    private void PrintDamageReport(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot)
        {
            return;
        }

        ulong playerSlot = (ulong)player.Slot;

        foreach (var opponent in Utilities.GetPlayers())
        {
            if (opponent == null || !opponent.IsValid || opponent.Slot == player.Slot || opponent.TeamNum == player.TeamNum)
            {
                continue;
            }

            ulong opponentSlot = (ulong)opponent.Slot;

            int damageDealt = 0, hitsDealt = 0;
            if (_damageMatrix.TryGetValue(playerSlot, out var dealtMap) && dealtMap.TryGetValue(opponentSlot, out var dealtRecord))
            {
                damageDealt = dealtRecord.Damage;
                hitsDealt = dealtRecord.Hits;
            }

            int damageTaken = 0, hitsTaken = 0;
            if (_damageMatrix.TryGetValue(opponentSlot, out var takenMap) && takenMap.TryGetValue(playerSlot, out var takenRecord))
            {
                damageTaken = takenRecord.Damage;
                hitsTaken = takenRecord.Hits;
            }

            player.PrintToChat($" {ChatColors.Purple}[Aether^] {ChatColors.Green}to: {ChatColors.White}[{damageDealt}/{hitsDealt} hits], {ChatColors.Red}from: {ChatColors.White}[{damageTaken}/{hitsTaken} hits] - {ChatColors.Grey}{opponent.PlayerName}");
        }
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (victim == null || !victim.IsValid)
        {
            return HookResult.Continue;
        }

        string weapon = @event.Weapon.ToLower();

        if (attacker == null || attacker.SteamID == victim.SteamID)
        {
            string cause = weapon is "inferno" or "molotov" ? "Incendiary / Molotov" : "Self-Elimination";
            string description = $"**{victim.PlayerName}** killed themselves.";

            object[] fields =
            {
                new { name = "Victim", value = victim.PlayerName, inline = true },
                new { name = "Cause", value = cause, inline = true }
            };

            _ = SendDiscordEmbed("Suicide", description, 0xFC6A00, fields);
        }
        else if (weapon is "knife" or "taser")
        {
            string description = $"**{attacker.PlayerName}** killed **{victim.PlayerName}**!";

            object[] fields =
            {
                new { name = "Executor", value = attacker.PlayerName, inline = true },
                new { name = "Target", value = victim.PlayerName, inline = true }
            };

            _ = SendDiscordEmbed("Shame Event", description, 0xFF0000, fields);
        }

        return HookResult.Continue;
    }

    // ---------------------------------------------------------------
    // Discord webhook logging
    // ---------------------------------------------------------------

    private async Task SendDiscordEmbed(string title, string description, int color, object[] fields)
    {
        if (string.IsNullOrEmpty(_webhookUrl))
        {
            return;
        }

        try
        {
            var payload = new
            {
                embeds = new[]
                {
                    new
                    {
                        title,
                        description,
                        color,
                        fields,
                        timestamp = DateTime.UtcNow.ToString("O")
                    }
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(_webhookUrl, content);
        }
        catch
        {
            // Swallow webhook delivery failures — logging shouldn't crash the plugin.
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private CCSPlayerController? FindPlayerByName(string namePart)
    {
        return Utilities.GetPlayers()
            .FirstOrDefault(p => p != null && p.IsValid && p.PlayerName.Contains(namePart, StringComparison.OrdinalIgnoreCase));
    }
}

public class DamageRecord
{
    public int Damage { get; set; }
    public int Hits { get; set; }
}

// Steam IPublishedFileService/QueryFiles response models.
// Trimmed down to just the fields this plugin reads.
public class SteamWorkshopRoot
{
    public SteamWorkshopResponse? Response { get; set; }
}

public class SteamWorkshopResponse
{
    public List<WorkshopFileDetails>? Files { get; set; }
}

public class WorkshopFileDetails
{
    public string Title { get; set; } = "";
    public string PublishedFileId { get; set; } = "";
}