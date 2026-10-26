using System;
using System.Reflection;
using HarmonyLib;
using IPA;
using IPA.Config;
using IPA.Config.Stores;
using MultiplayerEnvironmentEditor.Configuration;
using MultiplayerEnvironmentEditor.UI;
using UnityEngine.SceneManagement;
using IPALogger = IPA.Logging.Logger;

namespace MultiplayerEnvironmentEditor
{
    [Plugin(RuntimeOptions.SingleStartInit)]
    public class Plugin
    {
        internal static IPALogger Log { get; private set; }
        private Harmony _harmony;

        [Init]
        public Plugin(IPALogger logger, Config conf)
        {
            Log = logger;
            PluginConfig.Instance = conf.Generated<PluginConfig>();
        }

        [OnStart]
        public void OnStart()
        {
            _harmony = new Harmony("com.example.MultiplayerEnvironmentEditor");
            try
            {
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception ex)
            {
                Log.Error("Patching failed: " + ex);
            }

            SceneManager.sceneUnloaded += HandleSceneUnloaded;
            SettingsRegistrar.Create();
            Log.Info("MultiplayerEnvironmentEditor loaded.");
        }

        [OnExit]
        public void OnExit()
        {
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            if (_harmony != null) _harmony.UnpatchSelf();
        }

        // Forget this song's state once the multiplayer arena goes away.
        private static void HandleSceneUnloaded(Scene scene)
        {
            if (SoloEnv.Active && scene.name == SoloEnv.MpSceneName)
                SoloEnv.Reset();
        }
    }
}
