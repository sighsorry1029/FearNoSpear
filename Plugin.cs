using System;
using System.IO;
using System.Linq;
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
    public sealed class FearNoSpearPlugin : BaseUnityPlugin
    {
        public const string Author = "sighsorry";
        public const string ModName = "FearNoSpear";
        public const string PluginGuid = $"{Author}.{ModName}";
        public const string PluginName = "FearNoSpear";
        public const string ModVersion = "1.0.1";
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

        private void Awake()
        {
            Log = Logger;

            bool saveOnSet = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;

            _serverConfigLocked = BindConfig("General", "Lock Configuration", Toggle.On,
                "Locks the synchronized gameplay settings to the server's config when the mod is installed on a server. Keep this on for multiplayer servers so every client uses the same spear rescue timing and ownership safety rules.");
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
            SaveWithRespectToConfigSet();
            _watcher?.Dispose();
            _harmony?.UnpatchSelf();
        }

        private void OnApplicationQuit()
        {
            IsShuttingDown = true;
        }

        internal ConfigEntry<T> BindConfig<T>(string group, string name, T value, string description, bool synchronizedSetting = true)
        {
            string syncText = synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]";
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, new ConfigDescription(description + syncText));
            SyncedConfigEntry<T> syncedConfigEntry = Sync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;
            return configEntry;
        }

        internal ConfigEntry<T> BindConfig<T>(string group, string name, T value, string description, AcceptableValueBase acceptableValues, bool synchronizedSetting = true)
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
        internal readonly ConfigEntry<bool> Enabled;

        internal static readonly bool NameFallback = true;
        internal static readonly bool TrackAllRespawnItemProjectilesForDebug = false;
        internal static readonly bool ExtendInitialTtl = true;
        internal static readonly bool RescueBeforeTtlExpiry = true;
        internal static readonly bool RescueOnUnexpectedDestroy = true;
        internal static readonly bool UseItemDropFallback = true;
        internal static readonly bool OnlyOwnerMayRescue = true;
        internal static readonly bool UseZdoClaimFlag = true;
        internal const float MinimumInitialTtlSeconds = 60f;
        internal const int MaxPinsPerCommand = 5;
        internal static readonly bool Verbose = false;

        internal readonly ConfigEntry<float> TtlRescueWindowSeconds;
        internal readonly ConfigEntry<bool> AllowLastKnownOwnerIfZNetViewInvalid;
        internal readonly ConfigEntry<float> LastKnownOwnerGraceSeconds;
        internal readonly ConfigEntry<string> ChatCommand;
        internal readonly ConfigEntry<bool> CleanDeathPins;

        internal FearNoSpearConfig(FearNoSpearPlugin plugin)
        {
            Enabled = plugin.BindConfig("General", "Enabled", true,
                "Master switch for all FearNoSpear behavior. When disabled, the mod does not track thrown spear projectiles, extend their TTL, or rescue stored spear item data.");

            ChatCommand = plugin.BindConfig("General", "ChatCommand", "!myspear",
                "Chat command used to pin the latest known tracked spear location on the minimap. The comparison is case-insensitive and the command is consumed locally instead of being sent to public chat. Server operators can change this value, such as !spear or !lostspear, and lock it through ServerSync. Leave it empty to disable the chat command.");

            CleanDeathPins = plugin.BindConfig("General", "CleanDeathPins", true,
                "Removes the vanilla death map pin when the local player's tombstone is recovered, and suppresses the death pin when a death creates no tombstone. This setting is synchronized so server operators can keep the same behavior for all clients.");

            TtlRescueWindowSeconds = plugin.BindConfig("Rescue", "TTLRescueWindowSeconds", 1f,
                "How close to projectile TTL expiry the mod should rescue a still-airborne tracked spear. A value of 1.0 means the stored spear item is respawned during the final second of projectile lifetime if no normal hit occurred. Increase this if projectiles are still being cleaned up before rescue; decrease it if rescued spears feel like they stop flying too early.");

            AllowLastKnownOwnerIfZNetViewInvalid = plugin.BindConfig("Rescue", "AllowLastKnownOwnerIfZNetViewInvalid", true,
                "Allows a rescue attempt when the projectile ZNetView has already become invalid, but only if this client was the most recent known owner. This helps recover spears lost during unload, ownership disruption, or network cleanup. Disable it if multiplayer testing shows duplicate rescued spears.");

            LastKnownOwnerGraceSeconds = plugin.BindConfig("Rescue", "LastKnownOwnerGraceSeconds", 2f,
                "Maximum age, in seconds, for the last-known owner state used by the invalid-ZNetView fallback. Lower values reduce duplicate-spawn risk but may miss late cleanup cases; higher values are more forgiving but less conservative in multiplayer.");
        }

        internal int GetMaxPinsPerCommand()
        {
            return MaxPinsPerCommand;
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
        internal static FieldInfo? F_terminalInput;
        internal static MethodInfo? M_spawnOnHit;
        internal static MethodInfo? M_itemDropDropItem;
        internal static MethodInfo? M_zNetViewGetZdo;
        internal static MethodInfo? M_zdoGetBool;
        internal static MethodInfo? M_zdoSetBool;
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
            F_terminalInput = AccessTools.Field(typeof(Terminal), "m_input");

            M_spawnOnHit = AccessTools.Method(typeof(Projectile), "SpawnOnHit",
                    new[] { typeof(GameObject), typeof(Collider), typeof(Vector3) })
                ?? AccessTools.GetDeclaredMethods(typeof(Projectile))
                    .FirstOrDefault(m => m.Name == "SpawnOnHit" && m.GetParameters().Any(p => p.ParameterType == typeof(Vector3)))
                ?? AccessTools.Method(typeof(Projectile), "SpawnOnHit");
            M_itemDropDropItem = AccessTools.Method(typeof(ItemDrop), "DropItem",
                new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(Vector3), typeof(Quaternion) });

            M_zNetViewGetZdo = AccessTools.Method(typeof(ZNetView), "GetZDO");
            M_zdoGetBool = AccessTools.Method(typeof(ZDO), "GetBool", new[] { typeof(string), typeof(bool) });
            M_zdoSetBool = AccessTools.Method(typeof(ZDO), "Set", new[] { typeof(string), typeof(bool) });
            F_minimapPins = AccessTools.Field(typeof(Minimap), "m_pins");

            WarnMissing(log, nameof(F_ttl), F_ttl);
            WarnMissing(log, nameof(F_vel), F_vel);
            WarnMissing(log, nameof(F_nview), F_nview);
            WarnMissing(log, nameof(F_didHit), F_didHit);
            WarnMissing(log, nameof(F_weapon), F_weapon);
            WarnMissing(log, nameof(F_spawnItem), F_spawnItem);
            WarnMissing(log, nameof(F_respawnItemOnHit), F_respawnItemOnHit);
            WarnMissing(log, nameof(F_terminalInput), F_terminalInput);
            WarnMissing(log, nameof(M_spawnOnHit), M_spawnOnHit);
            WarnMissing(log, nameof(M_itemDropDropItem), M_itemDropDropItem);
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
            if (FearNoSpearConfig.TrackAllRespawnItemProjectilesForDebug) return true;

            ItemDrop.ItemData? spawnItem = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_spawnItem, projectile, null);
            ItemDrop.ItemData? weapon = ReflectionCache.Get<ItemDrop.ItemData?>(ReflectionCache.F_weapon, projectile, null);

            if (IsSpearItem(spawnItem) || IsSpearItem(weapon)) return true;

            if (!FearNoSpearConfig.NameFallback) return false;

            string projectileName = projectile.name ?? string.Empty;
            return ContainsSpearToken(projectileName);
        }

        internal static bool IsSpearItem(ItemDrop.ItemData? item)
        {
            if (item?.m_shared == null) return false;

            string skill = item.m_shared.m_skillType.ToString();
            if (string.Equals(skill, "Spears", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(skill, "Spear", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!FearNoSpearConfig.NameFallback) return false;

            return ContainsSpearToken(item.m_shared.m_name);
        }

        private static bool ContainsSpearToken(string? value)
        {
            if (value == null || value.Length == 0) return false;
            return value.IndexOf("spear", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("$item_spear", StringComparison.OrdinalIgnoreCase) >= 0;
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
        private static void Postfix(Projectile __instance)
        {
            if (!FearNoSpearPlugin.Cfg.Enabled.Value) return;
            SpearSafetyTracker.GetOrArmIfTracked(__instance, "Projectile.Setup");
        }
    }

    [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
    internal static class ProjectileFixedUpdatePatch
    {
        private static bool Prefix(Projectile __instance)
        {
            if (!FearNoSpearPlugin.Cfg.Enabled.Value) return true;
            SpearSafetyTracker? tracker = __instance.GetComponent<SpearSafetyTracker>() ??
                                          SpearSafetyTracker.GetOrArmIfTracked(__instance, "Projectile.FixedUpdate");
            if (tracker == null) return true;

            bool rescuedAndDestroyed = tracker.TryTtlRescueAndDestroyIfNeeded();
            return !rescuedAndDestroyed;
        }
    }

    [HarmonyPatch(typeof(Chat), "SendInput")]
    internal static class ChatSendInputPatch
    {
        private static bool Prefix(Chat __instance)
        {
            return !SpearChatCommand.TryConsume(__instance);
        }
    }

    [HarmonyPatch(typeof(Humanoid), "Pickup", new[] { typeof(GameObject), typeof(bool), typeof(bool) })]
    internal static class HumanoidPickupPatch
    {
        private static void Prefix(Humanoid __instance, GameObject go, out PickedSpearState? __state)
        {
            __state = null;
            if (!FearNoSpearPlugin.Cfg.Enabled.Value) return;
            if (Player.m_localPlayer == null || __instance != Player.m_localPlayer) return;
            if (go == null) return;

            ItemDrop drop = go.GetComponent<ItemDrop>();
            if (drop == null || drop.m_itemData == null) return;
            if (!SpearProjectileDetector.IsSpearItem(drop.m_itemData)) return;

            __state = new PickedSpearState
            {
                ItemKey = SpearItemIdentity.BuildLocatorKey(drop.m_itemData),
                Position = go.transform.position
            };
        }

        private static void Postfix(bool __result, PickedSpearState? __state)
        {
            if (!__result || __state == null) return;
            SpearLocator.MarkSpearPickedUp(__state.ItemKey, __state.Position);
        }

        private sealed class PickedSpearState
        {
            internal string ItemKey = string.Empty;
            internal Vector3 Position;
        }
    }

    [HarmonyPatch(typeof(Game), "Start")]
    internal static class GameStartPatch
    {
        private static void Postfix()
        {
            SpearLocator.Clear();
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
            if (!FearNoSpearPlugin.Cfg.Enabled.Value) return;
            SpearLocator.UpdatePendingServerRequest();
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
            tracker?.MarkNormalHit("Projectile.OnHit");
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
            if (!FearNoSpearPlugin.Cfg.Enabled.Value) return;
            if (!FearNoSpearConfig.RescueOnUnexpectedDestroy) return;
            if (__0 == null) return;

            SpearSafetyTracker? tracker = __0.GetComponent<SpearSafetyTracker>();
            if (tracker == null)
            {
                Projectile? projectile = __0.GetComponent<Projectile>();
                if (projectile != null)
                {
                    tracker = SpearSafetyTracker.GetOrArmIfTracked(projectile, "ZNetScene.Destroy");
                }
            }

            tracker?.TryRescue("ZNetScene.Destroy before hit");
        }
    }
}
