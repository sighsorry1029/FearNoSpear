using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace FearNoSpear
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(ClanTombstoneCompatibility.PluginGuid,
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class FearNoSpearPlugin : BaseUnityPlugin
    {
        public const string Author = "sighsorry";
        public const string ModName = "FearNoSpear";
        public const string PluginGuid = $"{Author}.{ModName}";
        public const string PluginName = "FearNoSpear";
        public const string ModVersion = "1.1.0";
        public const string PluginVersion = ModVersion;

        internal static ManualLogSource Log = null!;
        internal static FearNoSpearConfig Cfg = null!;
        internal static bool IsShuttingDown { get; private set; }

        private static readonly ConfigSync Sync = new(PluginGuid)
        {
            DisplayName = PluginName,
            CurrentVersion = PluginVersion,
            MinimumRequiredVersion = PluginVersion
        };
        private static ConfigEntry<Toggle> _serverConfigLocked = null!;
        private static string ConfigFileName => $"{PluginGuid}.cfg";
        private static string ConfigFileFullPath => Path.Combine(Paths.ConfigPath, ConfigFileName);

        private Harmony? _harmony;
        private FileSystemWatcher? _watcher;
        private readonly object _reloadLock = new();
        private DateTime _lastConfigReloadTime;
        private const long ReloadDelayTicks = TimeSpan.TicksPerSecond;

        public enum Toggle
        {
            On = 1,
            Off = 0
        }

        public enum IndicatorStyle
        {
            BeamAndHud,
            Beam,
            Hud,
            Off
        }

        private void Awake()
        {
            Log = Logger;

            bool saveOnSet = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;

            _serverConfigLocked = BindConfig("General", "Lock Configuration", Toggle.On,
                "Locks OwnerOnlyTombstones to the server's config when the mod is installed on a server. Personal indicator and death-pin settings are not locked. Spear rescue and thrower-only auto-pickup use fixed rules and have no config switches.");
            Sync.AddLockingConfigEntry(_serverConfigLocked);

            Cfg = new FearNoSpearConfig(this);

            ReflectionCache.Initialize(Logger);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();
            SetupWatcher();

            Config.Save();
            Config.SaveOnConfigSet = saveOnSet;

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded");
        }

        private void OnDestroy()
        {
            IsShuttingDown = true;
            SpearIndicator.Clear();
            SpearLocator.Clear();
            SaveWithRespectToConfigSet();
            _watcher?.Dispose();
            _harmony?.UnpatchSelf();
        }

        private void OnApplicationQuit()
        {
            IsShuttingDown = true;
        }

        private void LateUpdate()
        {
            SpearIndicator.Update();
        }

        internal ConfigEntry<T> BindConfig<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true, AcceptableValueBase? acceptableValues = null)
        {
            string syncText = synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]";
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, new ConfigDescription(description + syncText, acceptableValues));
            SyncedConfigEntry<T> syncedConfigEntry = Sync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;
            return configEntry;
        }

        private void SetupWatcher()
        {
            _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName)
            {
                IncludeSubdirectories = true,
                SynchronizingObject = ThreadingHelper.SynchronizingObject,
                EnableRaisingEvents = true
            };
            _watcher.Changed += ReadConfigValues;
            _watcher.Created += ReadConfigValues;
            _watcher.Renamed += ReadConfigValues;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            DateTime now = DateTime.Now;
            if (now.Ticks - _lastConfigReloadTime.Ticks < ReloadDelayTicks) return;

            lock (_reloadLock)
            {
                if (!File.Exists(ConfigFileFullPath))
                {
                    Log.LogWarning("Config file does not exist. Skipping reload.");
                    return;
                }

                try
                {
                    Log.LogDebug("Reloading configuration...");
                    SaveWithRespectToConfigSet(reload: true);
                    Log.LogInfo("Configuration reload complete.");
                }
                catch (Exception ex)
                {
                    Log.LogError($"Error reloading configuration: {ex.Message}");
                }
            }

            _lastConfigReloadTime = now;
        }

        private void SaveWithRespectToConfigSet(bool reload = false)
        {
            bool originalSaveOnSet = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;
            if (reload) Config.Reload();
            Config.Save();
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
    }

    internal sealed class FearNoSpearConfig
    {
        internal const float MinimumInitialTtlSeconds = 60f;
        internal const int MaxLocationResults = 5;
        internal const int MaxTombstoneResults = 5;
        internal const int MaxIndicatorResults = MaxLocationResults + MaxTombstoneResults;

        internal readonly ConfigEntry<bool> CleanDeathPins;
        internal readonly ConfigEntry<bool> OwnerOnlyTombstones;
        internal readonly ConfigEntry<FearNoSpearPlugin.IndicatorStyle> SpearIndicatorStyle;
        internal readonly ConfigEntry<FearNoSpearPlugin.IndicatorStyle> TombstoneIndicatorStyle;
        internal readonly ConfigEntry<int> MaxDisplayedSpears;
        internal readonly ConfigEntry<int> MaxDisplayedTombstones;

        internal FearNoSpearConfig(FearNoSpearPlugin plugin)
        {
            SpearIndicatorStyle = plugin.BindConfig("General", "SpearIndicatorStyle", FearNoSpearPlugin.IndicatorStyle.BeamAndHud,
                "Display your thrown spears and supported SecondaryAttacks copied throws, including ImpactBurst. BeamAndHud (default): a 500-meter column plus an item icon and distance. Beam: column only, subject to render distance and terrain. Hud: icon, distance, and screen-edge directions, visible through terrain and beyond world rendering distance. Off: stops weapon indicators and weapon queries, not protection or tombstone indicators. No real lights or forced zone loading are used. This is a personal setting.", synchronizedSetting: false);

            MaxDisplayedSpears = plugin.BindConfig("General", "MaxDisplayedSpears", 1,
                "Maximum number of your landed or rescued thrown weapons to display, nearest first. Includes supported SecondaryAttacks copied throws. Range: 1 to 5; default: 1. Nearby drops are followed as they move; distant server positions refresh every 5 seconds. Weapons and tombstones share one request. Use SpearIndicatorStyle = Off to disable weapon indicators. This is a personal setting.", synchronizedSetting: false,
                acceptableValues: new AcceptableValueRange<int>(1, MaxLocationResults));

            TombstoneIndicatorStyle = plugin.BindConfig("General", "TombstoneIndicatorStyle", FearNoSpearPlugin.IndicatorStyle.BeamAndHud,
                "Shows your newest remaining tombstones in this world, up to MaxDisplayedTombstones. BeamAndHud (default), Beam, Hud, or Off. HUD markers use the death icon, distance on the right, and elapsed in-game days/hours below the icon, such as 2d 12h. Age uses the world's clock and day length, not real calendar time; sleeping advances it. Nearby positions follow the tombstone; distant positions refresh from the server every 5 seconds. Fully recovered tombstones disappear from the display. Independent of CleanDeathPins and map pins. This is a personal setting.", synchronizedSetting: false);

            MaxDisplayedTombstones = plugin.BindConfig("General", "MaxDisplayedTombstones", 1,
                "Maximum number of your remaining tombstones to display, newest first rather than nearest first. Range: 1 to 5; default: 1. Independent of MaxDisplayedSpears. Changing this count does not add server requests. Use TombstoneIndicatorStyle = Off to hide tombstone indicators. This is a personal setting.", synchronizedSetting: false,
                acceptableValues: new AcceptableValueRange<int>(1, MaxTombstoneResults));

            CleanDeathPins = plugin.BindConfig("General", "CleanDeathPins", true,
                "Removes your vanilla death map pin when your tombstone is recovered, and removes the pin after a short grace period when a death creates no tombstone. Affects your own map only. This personal preference is not synchronized or locked by the server.", synchronizedSetting: false);

            OwnerOnlyTombstones = plugin.BindConfig("General", "OwnerOnlyTombstones", true,
                "Prevents players from opening or auto-looting a tombstone owned by another player. A server administrator or the local host can bypass this mod's owner restriction while debug mode is active, including on a dedicated server. This does not enable debug mode or override other mods' locks, inventory capacity, or containers already in use. When the optional Clan mod is installed, members of the local player's active Clan roster, including Guests, may also recover it. Tombstones without a valid owner ID remain accessible. This setting is synchronized and enabled by default.");

        }

    }

    internal static class ReflectionCache
    {
        internal static FieldInfo? F_ttl;
        internal static FieldInfo? F_vel;
        internal static FieldInfo? F_nview;
        internal static FieldInfo? F_didHit;
        internal static FieldInfo? F_weapon;
        internal static FieldInfo? F_spawnItem;
        internal static FieldInfo? F_respawnItemOnHit;
        internal static FieldInfo? F_groundHitOnly;
        internal static FieldInfo? F_projectileOwner;
        internal static FieldInfo? F_characterNView;
        internal static FieldInfo? F_tombstoneContainer;
        internal static MethodInfo? M_spawnOnHit;
        internal static MethodInfo? M_tombstoneGetOwner;
        internal static FieldInfo? F_minimapPins;

        internal static void Initialize(ManualLogSource log)
        {
            F_ttl = AccessTools.Field(typeof(Projectile), "m_ttl");
            F_vel = AccessTools.Field(typeof(Projectile), "m_vel");
            F_nview = AccessTools.Field(typeof(Projectile), "m_nview");
            F_didHit = AccessTools.Field(typeof(Projectile), "m_didHit");
            F_weapon = AccessTools.Field(typeof(Projectile), "m_weapon");
            F_spawnItem = AccessTools.Field(typeof(Projectile), "m_spawnItem");
            F_respawnItemOnHit = AccessTools.Field(typeof(Projectile), "m_respawnItemOnHit");
            F_groundHitOnly = AccessTools.Field(typeof(Projectile), "m_groundHitOnly");
            F_projectileOwner = AccessTools.Field(typeof(Projectile), "m_owner");
            F_characterNView = AccessTools.Field(typeof(Character), "m_nview");
            F_tombstoneContainer = AccessTools.Field(typeof(TombStone), "m_container");

            M_spawnOnHit = AccessTools.Method(typeof(Projectile), "SpawnOnHit",
                new[] { typeof(GameObject), typeof(Collider), typeof(Vector3) });
            M_tombstoneGetOwner = AccessTools.Method(typeof(TombStone), "GetOwner");

            F_minimapPins = AccessTools.Field(typeof(Minimap), "m_pins");

            WarnMissing(log, nameof(F_ttl), F_ttl);
            WarnMissing(log, nameof(F_vel), F_vel);
            WarnMissing(log, nameof(F_nview), F_nview);
            WarnMissing(log, nameof(F_didHit), F_didHit);
            WarnMissing(log, nameof(F_weapon), F_weapon);
            WarnMissing(log, nameof(F_spawnItem), F_spawnItem);
            WarnMissing(log, nameof(F_respawnItemOnHit), F_respawnItemOnHit);
            WarnMissing(log, nameof(F_groundHitOnly), F_groundHitOnly);
            WarnMissing(log, nameof(F_projectileOwner), F_projectileOwner);
            WarnMissing(log, nameof(F_characterNView), F_characterNView);
            WarnMissing(log, nameof(F_tombstoneContainer), F_tombstoneContainer);
            WarnMissing(log, nameof(M_spawnOnHit), M_spawnOnHit);
            WarnMissing(log, nameof(M_tombstoneGetOwner), M_tombstoneGetOwner);
            WarnMissing(log, nameof(F_minimapPins), F_minimapPins);
        }

        private static void WarnMissing(ManualLogSource log, string label, MemberInfo? member)
        {
            if (member == null) log.LogWarning($"Reflection member not found: {label}");
        }

        internal static T Get<T>(FieldInfo? field, object target, T fallback)
        {
            if (field == null || target == null) return fallback;
            try
            {
                object? value = field.GetValue(target);
                return value is T typed ? typed : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        internal static void Set<T>(FieldInfo? field, object target, T value)
        {
            if (field == null || target == null) return;
            try
            {
                field.SetValue(target, value);
            }
            catch
            {
                // Keep this draft non-fatal across game builds.
            }
        }

        internal static ZNetView? GetNView(Projectile projectile)
        {
            return Get<ZNetView?>(F_nview, projectile, null);
        }

        internal static ZNetView? GetNView(Character character)
        {
            return Get<ZNetView?>(F_characterNView, character, null);
        }

        internal static Character? GetProjectileOwner(Projectile projectile)
        {
            return Get<Character?>(F_projectileOwner, projectile, null);
        }

        internal static Container? GetTombstoneContainer(TombStone tombstone)
        {
            return Get<Container?>(F_tombstoneContainer, tombstone, null);
        }

        internal static long GetTombstoneOwner(TombStone tombstone)
        {
            if (M_tombstoneGetOwner == null || tombstone == null) return 0L;
            try
            {
                object? value = M_tombstoneGetOwner.Invoke(tombstone, null);
                return value is long ownerId ? ownerId : 0L;
            }
            catch
            {
                return 0L;
            }
        }
    }

    internal static class SpearProjectileDetector
    {
        internal static bool IsRecoverableProjectile(Projectile projectile)
        {
            if (projectile == null) return false;
            bool respawn = ReflectionCache.Get(ReflectionCache.F_respawnItemOnHit, projectile, false);
            if (!respawn) return false;
            ItemDrop.ItemData? spawnItem = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_spawnItem, projectile, null);
            return spawnItem != null;
        }

        internal static bool IsTrackedSpearProjectile(Projectile projectile)
        {
            if (!IsRecoverableProjectile(projectile)) return false;
            ItemDrop.ItemData? spawnItem = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_spawnItem, projectile, null);
            ItemDrop.ItemData? weapon = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_weapon, projectile, null);

            if (IsSpearItem(spawnItem) || IsSpearItem(weapon)) return true;

            ZNetView? nview = ReflectionCache.GetNView(projectile);
            if (nview != null && nview.IsValid() &&
                nview.GetZDO().GetBool("SecondaryAttacks_CopiedThrowProjectile", false)) return true;

            string projectileName = projectile.name ?? string.Empty;
            return ContainsSpearToken(projectileName);
        }

        internal static bool IsSpearItem(ItemDrop.ItemData? item)
        {
            if (item?.m_shared == null) return false;

            if (item.m_shared.m_skillType == Skills.SkillType.Spears)
            {
                return true;
            }

            return ContainsSpearToken(item.m_shared.m_name);
        }

        private static bool ContainsSpearToken(string? value)
        {
            if (value == null || value.Length == 0) return false;
            return value.IndexOf("spear", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static string DescribeProjectile(Projectile projectile)
        {
            ItemDrop.ItemData? spawnItem = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_spawnItem, projectile, null);
            ItemDrop.ItemData? weapon = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_weapon, projectile, null);
            string spawnName = spawnItem?.m_shared?.m_name ?? "<null>";
            string weaponName = weapon?.m_shared?.m_name ?? "<null>";
            string skill = spawnItem?.m_shared?.m_skillType.ToString() ?? weapon?.m_shared?.m_skillType.ToString() ?? "<unknown>";
            return $"projectile={projectile.name}, spawnItem={spawnName}, weapon={weaponName}, skill={skill}";
        }
    }

    [HarmonyPatch(typeof(Projectile), "Setup")]
    internal static class ProjectileSetupPatch
    {
        [HarmonyAfter("sighsorry.SecondaryAttacks")]
        private static void Postfix(Projectile __instance)
        {
            SpearSafetyTracker.GetOrArmIfTracked(__instance);
        }
    }

    [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
    internal static class ProjectileFixedUpdatePatch
    {
        private static bool Prefix(Projectile __instance)
        {
            SpearSafetyTracker? tracker = __instance.GetComponent<SpearSafetyTracker>() ??
                                          SpearSafetyTracker.GetOrArmIfTracked(__instance);
            if (tracker == null) return true;

            bool rescuedAndDestroyed = tracker.TryTtlRescueAndDestroyIfNeeded();
            return !rescuedAndDestroyed;
        }
    }

    [HarmonyPatch(typeof(Humanoid), "Pickup", new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
    internal static class HumanoidPickupPatch
    {
        private static void Prefix(Humanoid __instance, GameObject go, out string? __state)
        {
            __state = null;
            if (Player.m_localPlayer == null || __instance != Player.m_localPlayer) return;
            if (go == null) return;

            ItemDrop drop = go.GetComponent<ItemDrop>();
            if (drop == null || drop.m_itemData == null) return;
            if (SpearThrowerMetadata.ReadFromDrop(drop) == 0L) return;

            __state = SpearItemIdentity.BuildDropRecordKey(drop);
        }

        private static void Postfix(bool __result, string? __state)
        {
            if (!__result || __state == null || __state.Length == 0) return;
            SpearLocator.ForgetRecoveredTarget(__state);
        }
    }

    [HarmonyPatch(typeof(Game), "Start")]
    internal static class GameStartPatch
    {
        private static void Postfix()
        {
            SpearLocator.Clear();
            SpearIndicator.Clear();
            DeathPinCleaner.Clear();
            SpearNetwork.ClearSession();
            SpearNetwork.RegisterRpcs();
        }
    }

    [HarmonyPatch(typeof(Game), "Update")]
    internal static class GameUpdatePatch
    {
        private static void Postfix()
        {
            DeathPinCleaner.UpdatePendingDeath();
            SpearLocator.Update();
        }
    }

    [HarmonyPatch(typeof(ZNet), "Start")]
    internal static class ZNetStartPatch
    {
        private static void Postfix()
        {
            SpearNetwork.RegisterRpcs();
        }
    }

    [HarmonyPatch]
    internal static class ProjectileOnHitPatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(Projectile), "OnHit") != null;
        }

        private static MethodBase? TargetMethod()
        {
            return AccessTools.Method(typeof(Projectile), "OnHit");
        }

        private static void Postfix(Projectile __instance)
        {
            if (!ReflectionCache.Get(ReflectionCache.F_didHit, __instance, false)) return;

            SpearSafetyTracker tracker = __instance.GetComponent<SpearSafetyTracker>();
            tracker?.MarkNormalHit();
        }
    }

    [HarmonyPatch]
    internal static class ZNetSceneDestroyPatch
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(ZNetScene), "Destroy", new[] { typeof(GameObject) }) != null;
        }

        private static MethodBase? TargetMethod()
        {
            return AccessTools.Method(typeof(ZNetScene), "Destroy", new[] { typeof(GameObject) });
        }

        private static void Prefix(GameObject __0)
        {
            if (__0 == null) return;

            SpearSafetyTracker? tracker = __0.GetComponent<SpearSafetyTracker>();
            if (tracker == null)
            {
                Projectile? projectile = __0.GetComponent<Projectile>();
                if (projectile != null)
                {
                    tracker = SpearSafetyTracker.GetOrArmIfTracked(projectile);
                }
            }

            tracker?.TryRescue("ZNetScene.Destroy before hit");
        }
    }
}
