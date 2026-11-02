namespace TarkovMonitor
{
    /// <summary>A map symbol from tarkov.dev that a marker can use.</summary>
    public sealed record MarkerIcon(string Key, string Name, string Keywords)
    {
        /// <summary>The picture, served by tarkov.dev (also reachable from inside its map frame).</summary>
        public string Url => MarkerIcons.UrlOf(Key);
    }

    /// <summary>
    /// The symbols the tarkov.dev map draws itself (public/maps/interactive in its repository),
    /// with an English name and extra search words, German ones included.
    /// </summary>
    public static class MarkerIcons
    {
        public static readonly IReadOnlyList<MarkerIcon> All = new List<MarkerIcon>
        {
            new("key", "Key", "schluessel schlüssel door tür"),
            new("keycard", "Keycard", "karte card schluessel schlüssel"),
            new("lock", "Lock", "locked door tür schloss verschlossen"),
            new("quest_item", "Quest item", "quest gegenstand item"),
            new("quest_objective", "Quest objective", "quest ziel objective"),
            new("loose_loot", "Loose loot", "loot beute"),
            new("container_safe", "Safe", "tresor safe"),
            new("container_pc-block", "PC block", "computer pc"),
            new("container_cash-register", "Cash register", "kasse geld"),
            new("container_weapon-box", "Weapon box", "waffe waffenkiste"),
            new("container_grenade-box", "Grenade box", "granate"),
            new("container_wooden-ammo-box", "Ammo box", "munition ammo"),
            new("container_medbag", "Medbag", "medizin medic"),
            new("container_medcase", "Medcase", "medizin medic"),
            new("container_toolbox", "Toolbox", "werkzeug"),
            new("container_jacket", "Jacket", "jacke"),
            new("container_duffle-bag", "Duffle bag", "tasche sack"),
            new("container_drawer", "Drawer", "schublade"),
            new("container_plastic-suitcase", "Plastic suitcase", "koffer"),
            new("container_crate", "Crate", "kiste box"),
            new("container_wooden-crate", "Wooden crate", "holzkiste kiste"),
            new("container_ground-cache", "Ground cache", "versteck boden cache"),
            new("container_buried-barrel-cache", "Buried barrel", "fass versteck cache"),
            new("container_dead-scav", "Dead scav", "leiche toter scav"),
            new("container_festive-airdrop-supply-crate", "Airdrop crate", "airdrop abwurf"),
            new("extract_pmc", "PMC extract", "ausgang exit extract"),
            new("extract_scav", "Scav extract", "ausgang exit extract"),
            new("extract_shared", "Shared extract", "ausgang exit extract"),
            new("extract_transit", "Transit", "transit uebergang übergang"),
            new("spawn_pmc", "PMC spawn", "spawn"),
            new("spawn_scav", "Scav spawn", "spawn"),
            new("spawn_sniper_scav", "Sniper scav", "sniper scharfschuetze scharfschütze"),
            new("spawn_boss", "Boss", "boss"),
            new("spawn_rogue", "Rogue", "rogue"),
            new("spawn_bloodhound", "Bloodhound", "bloodhound"),
            new("spawn_black-div", "Black division", "black division"),
            new("spawn_af", "Armed forces", "militaer militär soldat"),
            new("spawn_cultist-priest", "Cultist", "kultist cultist"),
            new("hazard", "Hazard", "gefahr danger"),
            new("hazard_mortar", "Mortar", "moerser mörser artillerie"),
            new("stationarygun", "Stationary gun", "geschuetz geschütz mg"),
            new("switch", "Switch", "schalter hebel"),
            new("btr_stop", "BTR stop", "btr haltestelle"),
        };

        private static readonly HashSet<string> Keys = new(All.Select(icon => icon.Key));

        public static string UrlOf(string key) => $"https://tarkov.dev/maps/interactive/{key}.png";

        /// <summary>True for a tarkov.dev symbol; older markers may hold a text glyph instead.</summary>
        public static bool IsKnown(string? key) => key != null && Keys.Contains(key);
    }
}
