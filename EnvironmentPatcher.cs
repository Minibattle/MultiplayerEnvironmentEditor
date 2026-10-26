using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MultiplayerEnvironmentEditor.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;

namespace MultiplayerEnvironmentEditor
{
    /// <summary>
    /// Shared state for one multiplayer song.
    ///
    /// A singleplayer environment cannot simply be loaded next to the multiplayer one: its
    /// scripts get injected from the SCENE container, but in multiplayer the things they need
    /// (ColorManager and friends) are only bound inside each player's own container. That
    /// mismatch is what froze the game on a black screen.
    ///
    /// So the environment is withheld from the scene container and moved wholesale into the
    /// local player's container instead. This mirrors how MultiplayerExtensions did it.
    /// </summary>
    internal static class SoloEnv
    {
        public static bool Active;
        public static EnvironmentInfoSO RealMultiplayerEnv;
        public static EnvironmentInfoSO ChosenEnv;
        public static ColorScheme MergedScheme;
        public static bool HideNotes;
        public static bool HidePlayers;
        public static bool VivifyMap;
        public static int PlayerCount;

        /// <summary>Angle values are reused to carry each player's offset from you.</summary>
        public const float SlotScale = 0.01f;

        /// <summary>Line everyone up beside you once a lobby is bigger than a duel.</summary>
        public static bool SideBySide
        {
            get { return Active && PlayerCount > 2; }
        }
        public static string SpSceneName;
        public static string MpSceneName = "MultiplayerEnvironment";

        public static readonly List<MonoBehaviour> Behaviours = new List<MonoBehaviour>();
        public static readonly List<InstallerBase> NormalInstallers = new List<InstallerBase>();
        public static readonly List<Type> NormalInstallerTypes = new List<Type>();
        public static readonly List<ScriptableObjectInstaller> ScriptableObjectInstallers = new List<ScriptableObjectInstaller>();
        public static readonly List<MonoInstaller> MonoInstallers = new List<MonoInstaller>();
        public static readonly List<MonoInstaller> InstallerPrefabs = new List<MonoInstaller>();
        public static readonly List<GameObject> ObjectsToEnable = new List<GameObject>();

        public static void Reset()
        {
            Active = false;
            Behaviours.Clear();
            NormalInstallers.Clear();
            NormalInstallerTypes.Clear();
            ScriptableObjectInstallers.Clear();
            MonoInstallers.Clear();
            InstallerPrefabs.Clear();
            ObjectsToEnable.Clear();
            MergedScheme = null;
            HideNotes = false;
            HidePlayers = false;
            VivifyMap = false;
            PlayerCount = 0;
            SoloEnvLevelEndActions.Current = null;
        }

        public static bool IsLocalActivePlayer(Context ctx)
        {
            return ctx != null && ctx.transform != null &&
                   ctx.transform.name.IndexOf("LocalActivePlayer", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void Log(string msg)
        {
            if (PluginConfig.Instance.Diagnostics) Plugin.Log.Info(msg);
        }
    }

    internal static class EnvLookup
    {
        private static EnvironmentsListModel _allEnvironments;

        /// <summary>The environment this beatmap asks for, exactly as singleplayer would pick it.</summary>
        public static EnvironmentInfoSO ForBeatmap(BeatmapLevel level, BeatmapKey key)
        {
            if (level == null) return null;

            string name = level.GetEnvironmentName(key.beatmapCharacteristic, key.difficulty);
            if (string.IsNullOrEmpty(name)) return null;
            return Resolve(name);
        }

        public static EnvironmentInfoSO Resolve(string serializedName)
        {
            if (string.IsNullOrEmpty(serializedName)) return null;

            var loaded = Resources.FindObjectsOfTypeAll<EnvironmentInfoSO>()
                .FirstOrDefault(e => e != null && e.serializedName == serializedName);
            if (loaded != null) return loaded;

            // The map may want an environment that is not in memory yet; ask the game for the
            // full list, which is built once and then reused.
            try
            {
                if (_allEnvironments == null)
                    _allEnvironments = EnvironmentsListModel.CreateFromAddressables();
                if (_allEnvironments != null)
                    return _allEnvironments.GetEnvironmentInfoBySerializedNameSafe(serializedName);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
            }
            return null;
        }
    }

    /// <summary>
    /// Reads a map's declared requirements through SongCore. Done reflectively so the mod still
    /// loads and works if SongCore is missing or its API moves.
    /// </summary>
    internal static class MapRequirements
    {
        private static bool _looked;
        private static MethodInfo _retrieve;

        public static bool Matches(BeatmapLevel level, BeatmapKey key, string list)
        {
            var wanted = list
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalise)
                .Where(n => n.Length > 0)
                .ToArray();
            if (wanted.Length == 0 || level == null) return false;

            foreach (var declared in Declared(level, key))
            {
                var norm = Normalise(declared);
                if (norm.Length == 0) continue;
                if (wanted.Any(w => norm.Contains(w)))
                {
                    SoloEnv.Log("Map requires '" + declared + "'; hiding the other players.");
                    return true;
                }
            }
            return false;
        }

        private static string Normalise(string s)
        {
            return s == null ? "" : s.Trim().Replace(" ", "").Replace("_", "").ToLowerInvariant();
        }

        private static IEnumerable<string> Declared(BeatmapLevel level, BeatmapKey key)
        {
            var none = new string[0];
            try
            {
                if (!_looked)
                {
                    _looked = true;
                    var type = GameReflection.FindTypeWhere(t => t.FullName == "SongCore.Collections");
                    if (type == null)
                    {
                        SoloEnv.Log("SongCore not present; map requirements cannot be checked.");
                        return none;
                    }
                    _retrieve = AccessTools.Method(type, "RetrieveDifficultyData");
                }
                if (_retrieve == null) return none;

                var data = _retrieve.Invoke(null, new object[] { level, key });
                if (data == null) return none;

                var extra = AccessTools.Field(data.GetType(), "additionalDifficultyData");
                var req = extra == null ? null : extra.GetValue(data);
                if (req == null) return none;

                var result = new List<string>();
                foreach (var fieldName in new[] { "_requirements", "_suggestions" })
                {
                    var f = AccessTools.Field(req.GetType(), fieldName);
                    var vals = f == null ? null : f.GetValue(req) as string[];
                    if (vals != null) result.AddRange(vals);
                }
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
                return none;
            }
        }
    }

    /// <summary>
    /// Multiplayer has no ILevelEndActions, but singleplayer environments expect one.
    /// The patches below drive it from the local player's own finish/fail calls.
    /// </summary>
    internal class SoloEnvLevelEndActions : ILevelEndActions
    {
        internal static SoloEnvLevelEndActions Current;

        public event Action levelFinishedEvent;
        public event Action levelFailedEvent;

        public SoloEnvLevelEndActions() { Current = this; }

        internal void RaiseFinished()
        {
            var h = levelFinishedEvent; if (h != null) h();
        }

        internal void RaiseFailed()
        {
            var h = levelFailedEvent; if (h != null) h();
        }
    }

    [HarmonyPatch(typeof(MultiplayerLocalActivePlayerFacade), "ReportPlayerDidFinish")]
    internal static class PlayerFinishedPatch
    {
        private static void Prefix()
        {
            var c = SoloEnvLevelEndActions.Current;
            if (c != null) c.RaiseFinished();
        }
    }

    [HarmonyPatch(typeof(MultiplayerLocalActivePlayerFacade), "ReportPlayerNetworkDidFailed")]
    internal static class PlayerFailedPatch
    {
        private static void Prefix()
        {
            var c = SoloEnvLevelEndActions.Current;
            if (c != null) c.RaiseFailed();
        }
    }

    // ---------------------------------------------------------------- 1. choose the environment

    /// <summary>
    /// Makes the multiplayer transition build its scene list around the chosen singleplayer
    /// environment instead of the arena. The arena scene is added back in AddArenaScenePatch.
    /// </summary>
    [HarmonyPatch(typeof(MultiplayerLevelScenesTransitionSetupDataSO), "InitAndSetupScenes")]
    internal static class ChooseEnvironmentPatch
    {
        private static readonly MethodInfo GetOrLoad =
            AccessTools.Method(typeof(MultiplayerLevelScenesTransitionSetupDataSO), "GetOrLoadMultiplayerEnvironmentInfo");
        private static readonly FieldInfo LoadedEnv =
            AccessTools.Field(typeof(MultiplayerLevelScenesTransitionSetupDataSO), "_loadedMultiplayerEnvironmentInfo");

        private static void Prefix(MultiplayerLevelScenesTransitionSetupDataSO __instance)
        {
            SoloEnv.Reset();
            try
            {
                if (GetOrLoad == null || LoadedEnv == null)
                {
                    Plugin.Log.Error("Multiplayer environment members not found; mod inactive.");
                    return;
                }

                // Remember the genuine arena. Prefer the value the game has already loaded: calling
                // the loader ourselves opens a second Addressables handle on the same asset, which
                // makes a later song fail with "AssetReference that has already been loaded".
                var alreadyLoaded = LoadedEnv.GetValue(__instance) as EnvironmentInfoSO;
                if (alreadyLoaded != null)
                    SoloEnv.RealMultiplayerEnv = alreadyLoaded;
                else if (SoloEnv.RealMultiplayerEnv == null)
                    SoloEnv.RealMultiplayerEnv = GetOrLoad.Invoke(__instance, null) as EnvironmentInfoSO;

                if (!PluginConfig.Instance.Enabled)
                {
                    Restore(__instance);
                    return;
                }

                if (SoloEnv.RealMultiplayerEnv == null)
                {
                    Plugin.Log.Warn("Could not read the multiplayer arena environment; leaving this song alone.");
                    return;
                }

                // Use whatever environment the map itself asks for, the same as singleplayer does.
                var sp = EnvLookup.ForBeatmap(__instance.beatmapLevel, __instance.beatmapKey);
                if (sp == null || sp.sceneInfo == null)
                {
                    Plugin.Log.Warn("Could not work out this map's environment; leaving this song alone.");
                    Restore(__instance);
                    return;
                }

                SoloEnv.SpSceneName = sp.sceneInfo.sceneName;
                SoloEnv.MpSceneName = SoloEnv.RealMultiplayerEnv.sceneInfo != null
                    ? SoloEnv.RealMultiplayerEnv.sceneInfo.sceneName
                    : "MultiplayerEnvironment";

                if (SoloEnv.SpSceneName == SoloEnv.MpSceneName)
                {
                    Restore(__instance);
                    return;
                }

                SoloEnv.HideNotes = PluginConfig.Instance.HideOtherPlayers ||
                                    MapRequirements.Matches(__instance.beatmapLevel, __instance.beatmapKey,
                                                            PluginConfig.Instance.AutoHideForRequirements);
                SoloEnv.VivifyMap = MapRequirements.Matches(__instance.beatmapLevel, __instance.beatmapKey,
                                                            PluginConfig.Instance.AutoHideHologramForRequirements);
                SoloEnv.HidePlayers = PluginConfig.Instance.HidePlayersEntirely;
                SoloEnv.ChosenEnv = sp;
                LoadedEnv.SetValue(__instance, sp);
                SoloEnv.Active = true;
                SoloEnv.Log("Using '" + sp.serializedName + "' (scene " + SoloEnv.SpSceneName +
                                ") alongside arena scene " + SoloEnv.MpSceneName + ".");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
                SoloEnv.Active = false;
            }
        }

        private static void Restore(MultiplayerLevelScenesTransitionSetupDataSO inst)
        {
            if (LoadedEnv != null && SoloEnv.RealMultiplayerEnv != null)
                LoadedEnv.SetValue(inst, SoloEnv.RealMultiplayerEnv);
        }

        /// <summary>
        /// Always hand the arena environment back once the scenes are set up. This object is a
        /// ScriptableObject that outlives the song, and leaving our substitute in the field breaks
        /// the arena asset's load/release cycle - the next song then fails inside Init with a
        /// NullReferenceException and never reaches gameplay.
        /// </summary>
        private static void Finalizer(MultiplayerLevelScenesTransitionSetupDataSO __instance)
        {
            Restore(__instance);
        }
    }

    /// <summary>
    /// Adds the multiplayer arena scene back, so other players and the leader hologram stay.
    ///
    /// This runs as a postfix on InitAndSetupScenes and writes the scene array directly, because
    /// the setup data assigns its own 'scenes' property rather than going through the base Init.
    /// If the arena cannot be re-added the whole feature switches off for this song: without it
    /// nothing provides LightWithIdManager, and the environment's lights throw every frame.
    /// </summary>
    [HarmonyPatch(typeof(MultiplayerLevelScenesTransitionSetupDataSO), "InitAndSetupScenes")]
    internal static class AddArenaScenePatch
    {
        private static readonly FieldInfo ScenesField =
            AccessTools.Field(typeof(ScenesTransitionSetupDataSO), "<scenes>k__BackingField");

        private static void Postfix(MultiplayerLevelScenesTransitionSetupDataSO __instance)
        {
            if (!SoloEnv.Active || SoloEnv.RealMultiplayerEnv == null) return;

            try
            {
                var arena = SoloEnv.RealMultiplayerEnv.sceneInfo;
                var scenes = __instance.scenes;

                if (arena == null || scenes == null || ScenesField == null)
                {
                    Plugin.Log.Error("Could not add the arena scene back; leaving this song alone.");
                    SoloEnv.Active = false;
                    return;
                }

                if (!scenes.Any(s => s == arena))
                {
                    scenes = scenes.Concat(new[] { arena }).ToArray();
                    ScenesField.SetValue(__instance, scenes);
                }

                SoloEnv.Log("Scene list: " +
                    string.Join(", ", scenes.Select(s => s == null ? "null" : s.sceneName).ToArray()));

                ApplyColorScheme(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
                SoloEnv.Active = false;
            }
        }

        /// <summary>
        /// Swap the multiplayer colour scheme for the environment's own, so sabers, notes and
        /// obstacles match singleplayer. Setting it on the gameplay setup data covers everything
        /// downstream, rather than recolouring each piece separately.
        /// </summary>
        private static void ApplyColorScheme(MultiplayerLevelScenesTransitionSetupDataSO __instance)
        {
            SoloEnv.MergedScheme = null;

            var gcsdProp = AccessTools.Property(__instance.GetType(), "gameplayCoreSceneSetupData");
            var gcsd = gcsdProp == null ? null : gcsdProp.GetValue(__instance, null);
            if (gcsd == null)
            {
                Plugin.Log.Warn("No gameplay setup data yet; colours left as they are.");
                return;
            }

            var field = AccessTools.Field(gcsd.GetType(), "colorScheme");
            if (field == null) return;

            var current = field.GetValue(gcsd) as ColorScheme;
            var merged = BuildScheme(current);
            if (merged == null) return;

            field.SetValue(gcsd, merged);
            SoloEnv.MergedScheme = merged;
            SoloEnv.Log("Colour scheme: '" + (current == null ? "none" : current.colorSchemeId) +
                            "' -> '" + merged.colorSchemeId + "'.");
        }

        /// <summary>
        /// Builds the scheme to play with. A map (or the player) may have supplied its own colours,
        /// and those must win - so only the plain, untouched arena scheme is replaced outright.
        /// Anything else is merged, which keeps whatever it overrides and fills the rest from the
        /// environment.
        /// </summary>
        private static ColorScheme BuildScheme(ColorScheme current)
        {
            var env = SoloEnv.ChosenEnv;
            var envScheme = env == null ? null : env.colorScheme;
            if (envScheme == null)
            {
                Plugin.Log.Warn("'" + (env == null ? "?" : env.serializedName) +
                                "' has no colour scheme of its own; colours left as they are.");
                return null;
            }

            if (current == null) return envScheme.colorScheme;

            var arena = SoloEnv.RealMultiplayerEnv;
            var arenaScheme = arena == null ? null : arena.colorScheme;
            bool untouchedArenaScheme =
                arenaScheme != null && arenaScheme.colorScheme != null &&
                current.colorSchemeId == arenaScheme.colorScheme.colorSchemeId;

            if (untouchedArenaScheme) return envScheme.colorScheme;

            SoloEnv.Log("Keeping custom colours from scheme '" + current.colorSchemeId +
                        "' (notes=" + current.overrideNotes + ", lights=" + current.overrideLights + ").");
            return new ColorScheme(current, envScheme);
        }
    }

    // ------------------------------------------- 2. withhold the environment from the scene container

    /// <summary>
    /// Stops the singleplayer environment's scripts being injected by the scene container, and
    /// keeps them for the local player's container instead.
    /// </summary>
    [HarmonyPatch(typeof(SceneDecoratorContext), "GetInjectableMonoBehaviours")]
    internal static class WithholdBehavioursPatch
    {
        private static readonly FieldInfo ZbComponents = AccessTools.Field(typeof(ZenjectBinding), "_components");

        private static void Postfix(SceneDecoratorContext __instance, List<MonoBehaviour> monoBehaviours)
        {
            if (!SoloEnv.Active || monoBehaviours == null) return;
            if (__instance.gameObject.scene.name != SoloEnv.SpSceneName) return;

            // The arena provides the light manager, so a second one is a bind conflict. Only drop
            // ours when the arena is genuinely loaded - otherwise nothing would provide it at all
            // and every light script in the environment throws on each frame.
            var arenaScene = SceneManager.GetSceneByName(SoloEnv.MpSceneName);
            if (arenaScene.IsValid() && arenaScene.isLoaded)
                monoBehaviours.RemoveAll(IsLightManagerBinding);
            else
                Plugin.Log.Warn("Arena scene '" + SoloEnv.MpSceneName +
                                "' is not loaded; keeping the environment's own light manager.");

            SoloEnv.Behaviours.Clear();
            SoloEnv.Behaviours.AddRange(monoBehaviours);
            monoBehaviours.Clear();
            SoloEnv.Log("Withheld " + SoloEnv.Behaviours.Count + " environment behaviours from the scene container.");
        }

        private static bool IsLightManagerBinding(MonoBehaviour b)
        {
            var zb = b as ZenjectBinding;
            if (zb == null || ZbComponents == null) return false;
            var comps = ZbComponents.GetValue(zb) as Component[];
            return comps != null && comps.Any(c => c is LightWithIdManager);
        }
    }

    /// <summary>Holds back the environment's own installers so they can run in the player container.</summary>
    [HarmonyPatch(typeof(SceneDecoratorContext), "InstallDecoratorInstallers")]
    internal static class WithholdInstallersPatch
    {
        private static readonly FieldInfo FNormal = AccessTools.Field(typeof(Context), "_normalInstallers");
        private static readonly FieldInfo FNormalTypes = AccessTools.Field(typeof(Context), "_normalInstallerTypes");
        private static readonly FieldInfo FScriptable = AccessTools.Field(typeof(Context), "_scriptableObjectInstallers");
        private static readonly FieldInfo FMono = AccessTools.Field(typeof(Context), "_monoInstallers");
        private static readonly FieldInfo FPrefabs = AccessTools.Field(typeof(Context), "_installerPrefabs");

        private static void Prefix(SceneDecoratorContext __instance)
        {
            if (!SoloEnv.Active) return;
            if (__instance.gameObject.scene.name != SoloEnv.SpSceneName) return;

            Move(FNormal, __instance, SoloEnv.NormalInstallers);
            Move(FNormalTypes, __instance, SoloEnv.NormalInstallerTypes);
            Move(FScriptable, __instance, SoloEnv.ScriptableObjectInstallers);
            Move(FMono, __instance, SoloEnv.MonoInstallers);
            Move(FPrefabs, __instance, SoloEnv.InstallerPrefabs);
            SoloEnv.Log("Withheld environment installers from the scene container.");
        }

        private static void Move<T>(FieldInfo field, object instance, List<T> into)
        {
            if (field == null) return;
            var live = field.GetValue(instance) as List<T>;
            if (live == null) return;
            into.Clear();
            into.AddRange(live);
            live.Clear();
        }
    }

    /// <summary>Keeps the environment's objects switched off until the player container owns them.</summary>
    [HarmonyPatch(typeof(GameScenesManager), "ActivatePresentedSceneRootObjects")]
    internal static class DelayActivationPatch
    {
        private static void Prefix(List<string> scenesToPresent)
        {
            if (!SoloEnv.Active || scenesToPresent == null) return;
            if (!scenesToPresent.Contains(SoloEnv.SpSceneName)) return;
            if (!scenesToPresent.Contains(SoloEnv.MpSceneName)) return;

            var scene = SceneManager.GetSceneByName(SoloEnv.SpSceneName);
            if (!scene.IsValid()) return;

            SoloEnv.ObjectsToEnable.Clear();
            SoloEnv.ObjectsToEnable.AddRange(scene.GetRootGameObjects());
            scenesToPresent.Remove(SoloEnv.SpSceneName);
            SoloEnv.Log("Held back " + SoloEnv.ObjectsToEnable.Count + " environment root objects.");
        }
    }

    // ------------------------------------------- 3. hand the environment to the local player container

    [HarmonyPatch(typeof(GameObjectContext), "GetInjectableMonoBehaviours")]
    internal static class InjectIntoPlayerPatch
    {
        private static void Postfix(GameObjectContext __instance, List<MonoBehaviour> monoBehaviours)
        {
            if (!SoloEnv.Active || monoBehaviours == null) return;
            if (!SoloEnv.IsLocalActivePlayer(__instance)) return;

            monoBehaviours.AddRange(SoloEnv.Behaviours);
            SoloEnv.Log("Injecting " + SoloEnv.Behaviours.Count + " environment behaviours into the local player.");
        }
    }

    [HarmonyPatch(typeof(Context), "InstallInstallers", new Type[] {
        typeof(List<InstallerBase>), typeof(List<Type>), typeof(List<ScriptableObjectInstaller>),
        typeof(List<MonoInstaller>), typeof(List<MonoInstaller>) })]
    internal static class InstallIntoPlayerPatch
    {
        private static void Prefix(
            Context __instance,
            List<InstallerBase> normalInstallers,
            List<Type> normalInstallerTypes,
            List<ScriptableObjectInstaller> scriptableObjectInstallers,
            List<MonoInstaller> installers,
            List<MonoInstaller> installerPrefabs)
        {
            if (!SoloEnv.Active) return;
            if (!(__instance is GameObjectContext) || !SoloEnv.IsLocalActivePlayer(__instance)) return;

            try
            {
                var container = __instance.Container;
                if (container != null)
                {
                    BindEnvironmentSupport(container, __instance);
                    SwapHud(container, __instance);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
            }

            if (normalInstallers != null) normalInstallers.AddRange(SoloEnv.NormalInstallers);
            if (normalInstallerTypes != null) normalInstallerTypes.AddRange(SoloEnv.NormalInstallerTypes);
            if (scriptableObjectInstallers != null) scriptableObjectInstallers.AddRange(SoloEnv.ScriptableObjectInstallers);
            if (installers != null) installers.AddRange(SoloEnv.MonoInstallers);
            if (installerPrefabs != null) installerPrefabs.AddRange(SoloEnv.InstallerPrefabs);
            SoloEnv.Log("Installed environment installers into the local player container.");
        }

        /// <summary>Bindings a singleplayer environment expects that multiplayer never makes.</summary>
        private static void BindEnvironmentSupport(DiContainer container, Context ctx)
        {
            if (!container.HasBinding(typeof(EnvironmentContext)))
                container.Bind<EnvironmentContext>().FromInstance(EnvironmentContext.Gameplay).AsSingle();

            if (!container.HasBinding(typeof(ILevelEndActions)))
                container.Bind<ILevelEndActions>()
                         .FromInstance(new SoloEnvLevelEndActions())
                         .AsSingle();
        }

        /// <summary>Use the singleplayer HUD and hide the multiplayer one, so they don't overlap.</summary>
        private static void SwapHud(DiContainer container, Context ctx)
        {
            if (!PluginConfig.Instance.UseSoloHud) return;

            var soloHud = SoloEnv.Behaviours.OfType<CoreGameHUDController>().FirstOrDefault();
            if (soloHud == null) return;

            var mpHud = ctx.GetComponentInChildren<CoreGameHUDController>(true);
            if (mpHud != null && mpHud != soloHud) mpHud.gameObject.SetActive(false);

            container.Unbind(typeof(CoreGameHUDController));
            container.Bind<CoreGameHUDController>().FromInstance(soloHud).AsSingle();

            var posHud = ctx.GetComponentInChildren<MultiplayerPositionHUDController>(true);
            if (posHud != null) posHud.transform.position += new Vector3(0f, 0.01f, 0f);
        }
    }

    /// <summary>Switches the environment on once it belongs to the local player's container.</summary>
    [HarmonyPatch(typeof(Context), "InstallSceneBindings")]
    internal static class ActivateEnvironmentPatch
    {
        private static readonly FieldInfo ActiveOnly =
            AccessTools.Field(typeof(MultiplayerLocalActivePlayerFacade), "_activeOnlyGameObjects");

        private static void Postfix(Context __instance)
        {
            if (!SoloEnv.Active) return;
            if (!(__instance is GameObjectContext) || !SoloEnv.IsLocalActivePlayer(__instance)) return;

            try
            {
                foreach (var go in SoloEnv.ObjectsToEnable)
                    if (go != null) go.SetActive(true);

                HideReplacedPlatformParts(__instance.transform);

                // Make the environment follow the player's own active/inactive lifetime.
                var facade = __instance.GetComponent<MultiplayerLocalActivePlayerFacade>();
                if (facade != null && ActiveOnly != null)
                {
                    var existing = ActiveOnly.GetValue(facade) as GameObject[] ?? new GameObject[0];
                    var merged = existing.Concat(SoloEnv.ObjectsToEnable.Where(g => g != null)).ToArray();
                    ActiveOnly.SetValue(facade, merged);
                }

                SoloEnv.Log("Solo environment activated inside the local player container.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
            }
        }

        private static void HideReplacedPlatformParts(Transform root)
        {
            var paths = PluginConfig.Instance.HideLocalPlayerObjects
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var raw in paths)
            {
                var path = raw.Trim();
                if (path.Length == 0) continue;
                var t = root.Find(path);
                if (t != null) t.gameObject.SetActive(false);
                else SoloEnv.Log("No object at '" + path + "' on the local player.");
            }
        }
    }

    // ---------------------------------------------------------------- 4. fix-ups

    /// <summary>The arena and the solo environment both run EnvironmentSceneSetup; the second is a conflict.</summary>
    [HarmonyPatch(typeof(EnvironmentSceneSetup), "InstallBindings")]
    internal static class SkipDuplicateEnvironmentSetupPatch
    {
        private static readonly PropertyInfo ContainerProp = AccessTools.Property(typeof(MonoInstallerBase), "Container");

        private static bool Prefix(EnvironmentSceneSetup __instance)
        {
            if (!SoloEnv.Active || ContainerProp == null) return true;

            var container = ContainerProp.GetValue(__instance, null) as DiContainer;
            var initData = AccessTools.Inner(typeof(EnvironmentBrandingManager), "InitData");
            if (container == null || initData == null) return true;

            if (container.HasBinding(initData))
            {
                SoloEnv.Log("Skipped a duplicate EnvironmentSceneSetup install.");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Multiplayer forces the arena's colour scheme on the lighting, so the singleplayer
    /// environment comes out in multiplayer colours. This hands the environment its own scheme.
    ///
    /// The light effects read their colours from the SimpleColorSO objects that
    /// EnvironmentColorManager owns, so updating the manager recolours everything already running.
    /// Re-running each effect's Start() instead throws inside SetColor, which is what broke here
    /// before.
    /// </summary>
    [HarmonyPatch(typeof(GameplayCoreInstaller), "InstallBindings")]
    internal static class SoloEnvColorsPatch
    {
        private static readonly PropertyInfo ContainerProp = AccessTools.Property(typeof(MonoInstallerBase), "Container");
        private static readonly MethodInfo InitColors = AccessTools.Method(typeof(EnvironmentColorManager), "InitColors");

        private static void Postfix(GameplayCoreInstaller __instance)
        {
            if (!SoloEnv.Active || ContainerProp == null) return;

            try
            {
                var container = ContainerProp.GetValue(__instance, null) as DiContainer;
                if (container == null) return;

                var colorManager = container.TryResolve(typeof(EnvironmentColorManager)) as EnvironmentColorManager;
                if (colorManager == null)
                {
                    Plugin.Log.Warn("No EnvironmentColorManager in this container; colours left as they are.");
                    return;
                }

                var scheme = SoloEnv.MergedScheme;
                if (scheme == null) return;

                colorManager.SetColorScheme(scheme);
                if (InitColors != null) InitColors.Invoke(colorManager, null);
                SoloEnv.Log("Applied the environment's own colour scheme to the lighting.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
            }
        }
    }

    /// <summary>Notes how many people are in the lobby, which decides the layout below.</summary>
    [HarmonyPatch(typeof(MultiplayerPlayerPlacement), "GetAngleBetweenPlayersWithEvenAdjustment")]
    internal static class PlayerCountPatch
    {
        private static void Postfix(int numberOfPlayers)
        {
            if (SoloEnv.PlayerCount == numberOfPlayers) return;
            SoloEnv.PlayerCount = numberOfPlayers;
            SoloEnv.Log("Lobby has " + numberOfPlayers + " player(s); side-by-side = " + SoloEnv.SideBySide + ".");
        }
    }

    /// <summary>
    /// Carries each player's offset from you in place of an angle, so the position patch below
    /// can lay everyone out in a row instead of around a circle.
    /// </summary>
    [HarmonyPatch(typeof(MultiplayerPlayerPlacement), "GetOuterCirclePositionAngleForPlayer")]
    internal static class PlayerSlotPatch
    {
        private static bool Prefix(int playerIndex, int localPlayerIndex, ref float __result)
        {
            if (!SoloEnv.SideBySide) return true;
            __result = (playerIndex - localPlayerIndex) * SoloEnv.SlotScale;
            return false;
        }
    }

    /// <summary>
    /// Places the other players. With three or more people the game spreads them around a wide
    /// circle, which looks wrong in a singleplayer environment, so they are lined up next to you
    /// instead - lower-numbered players to your left, higher to your right. Either way the spacing
    /// slider scales the result.
    /// </summary>
    [HarmonyPatch(typeof(MultiplayerPlayerPlacement), "GetPlayerWorldPosition")]
    internal static class PlayerPositionPatch
    {
        private static bool Prefix(float outerCirclePositionAngle, ref Vector3 __result)
        {
            if (!SoloEnv.SideBySide) return true;

            int slot = Mathf.RoundToInt(outerCirclePositionAngle / SoloEnv.SlotScale);
            __result = new Vector3(slot * PluginConfig.Instance.SideBySideSpacing, 0f, 0f);
            return false;
        }

        private static void Postfix(ref Vector3 __result)
        {
            if (!SoloEnv.Active) return;

            var spread = PluginConfig.Instance.OtherPlayerSpread;
            if (spread <= 0f || spread == 1f) return;

            // Scale sideways only: everyone keeps the side they were on and their order.
            __result = new Vector3(__result.x * spread, __result.y, __result.z);
        }
    }

    /// <summary>
    /// Keeps a connected player invisible. Their avatar and platform are built after the player's
    /// bindings are installed, so a single sweep at install time misses them - this re-checks on a
    /// slow timer and switches off anything new.
    /// </summary>
    internal class HideConnectedPlayer : MonoBehaviour
    {
        private const float Interval = 0.5f;
        private float _next;

        private void OnEnable()
        {
            _next = 0f;
        }

        private void LateUpdate()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + Interval;

            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
                if (renderer.enabled) renderer.enabled = false;
        }
    }

    /// <summary>Makes the other players and their platforms completely invisible.</summary>
    [HarmonyPatch(typeof(Context), "InstallSceneBindings")]
    internal static class HidePlayersEntirelyPatch
    {
        private static void Postfix(Context __instance)
        {
            if (!SoloEnv.Active || !SoloEnv.HidePlayers) return;

            var name = __instance.transform.name;
            if (name.IndexOf("ConnectedPlayer", StringComparison.OrdinalIgnoreCase) < 0) return;

            try
            {
                if (__instance.GetComponent<HideConnectedPlayer>() == null)
                    __instance.gameObject.AddComponent<HideConnectedPlayer>();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex);
            }
        }
    }

    /// <summary>Stops the hidden players' notes being spawned at all.</summary>
    [HarmonyPatch(typeof(MultiplayerConnectedPlayerFacade), "ResumeSpawning")]
    internal static class StopOtherPlayerNotesPatch
    {
        private static bool Prefix()
        {
            return !(SoloEnv.Active && (SoloEnv.HideNotes || SoloEnv.HidePlayers));
        }
    }

    /// <summary>
    /// Removes the cage the game keeps multiplayer avatars in. Head and hands are passed through
    /// untouched, so a player can move as far from their platform as they like - far enough to
    /// reach someone else, given a playspace mover - and arms stretch rather than snapping back.
    /// This matches the game's own NoAvatarPoseRestriction behaviour.
    /// </summary>
    [HarmonyPatch(typeof(BeatSaber.AvatarCore.LimitAvatarPoseRestriction), "RestrictPose")]
    internal static class UnlimitedMovementPatch
    {
        private static bool Prefix(
            Vector3 headPosition, Vector3 leftHandPosition, Vector3 rightHandPosition,
            ref Vector3 newHeadPosition, ref Vector3 newLeftHandPosition, ref Vector3 newRightHandPosition)
        {
            if (PluginConfig.Instance.VanillaPlayspaceLimits) return true;

            newHeadPosition = headPosition;
            newLeftHandPosition = leftHandPosition;
            newRightHandPosition = rightHandPosition;
            return false;
        }
    }

    /// <summary>
    /// Hides the first-place player's hologram. Forcing the animator's own "show" flag off keeps
    /// its state machine consistent, so later leader changes cannot bring it back.
    /// </summary>
    [HarmonyPatch(typeof(MultiplayerBigAvatarAnimator), "Animate")]
    internal static class HologramPatch
    {
        private static void Prefix(ref bool show)
        {
            if (Hidden) show = false;
        }

        private static bool Hidden
        {
            get
            {
                if (!PluginConfig.Instance.ShowHolograms) return true;
                return SoloEnv.Active && SoloEnv.VivifyMap;
            }
        }
    }


    /// <summary>
    /// Multiplayer keeps the song running while the in-game menu is open, so hiding the notes and
    /// walls just loses you the ones that pass by. This leaves them on screen.
    /// </summary>
    [HarmonyPatch(typeof(BeatmapObjectManager), "HideAllBeatmapObjects")]
    internal static class KeepObjectsVisiblePatch
    {
        private static bool Prefix(bool hide)
        {
            // Only block the hide; let anything that unhides run as normal.
            return !(hide && SoloEnv.Active && PluginConfig.Instance.KeepPlayingWhenInterrupted);
        }
    }

    /// <summary>
    /// Stops the SteamVR overlay taking input focus from throwing up the give-up menu. The song
    /// carries on without you for those few seconds, which beats forfeiting the whole match.
    /// </summary>
    [HarmonyPatch]
    internal static class IgnoreFocusLossPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var type = typeof(MultiplayerLocalActivePlayerInGameMenuController);
            var found = new List<MethodBase>();
            foreach (var name in new[] { "HandleInputFocusWasCaptured", "OnApplicationPause" })
            {
                var m = AccessTools.Method(type, name);
                if (m != null) found.Add(m);
            }
            return found;
        }

        private static bool Prepare()
        {
            return TargetMethods().Any();
        }

        private static bool Prefix()
        {
            return !PluginConfig.Instance.KeepPlayingWhenInterrupted;
        }
    }
}
