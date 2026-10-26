using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BeatSaberMarkupLanguage.Settings;
using UnityEngine;

namespace MultiplayerEnvironmentEditor.UI
{
    /// <summary>
    /// BSML's services aren't available when BSIPA calls OnStart (the menu's DI container doesn't
    /// exist yet), so keep retrying until the main menu is up, then register each UI piece once.
    /// </summary>
    internal class SettingsRegistrar : MonoBehaviour
    {
        public static void Create()
        {
            var go = new GameObject("MultiplayerEnvironmentEditor.SettingsRegistrar");
            DontDestroyOnLoad(go);
            go.AddComponent<SettingsRegistrar>();
        }

        private IEnumerator Start()
        {
            bool settingsDone = false, lobbyDone = false;

            for (int attempt = 0; attempt < 120 && !(settingsDone && lobbyDone); attempt++)
            {
                if (!settingsDone) settingsDone = TryRegister(() =>
                    BSMLSettings.Instance.AddSettingsMenu(
                        "MPEnviornment", "MultiplayerEnvironmentEditor.UI.settings.bsml", SettingsController.Instance),
                    "Settings menu");

                if (!lobbyDone) lobbyDone = TryRegister(AddLobbyTab, "Lobby mods tab");

                if (!(settingsDone && lobbyDone))
                    yield return new WaitForSeconds(1f);
            }
            Destroy(gameObject);
        }

        /// <summary>
        /// Adds the tab to BSML's GameplaySetup for online (lobby) menus. Uses reflection because
        /// the namespace of MenuType differs between BSML versions.
        /// </summary>
        private static void AddLobbyTab()
        {
            // The real BSML class (not some other type that happens to share the name)
            var gsType = GameReflection.FindTypeWhere(t =>
                t.Name == "GameplaySetup" &&
                t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == "AddTab"));
            if (gsType == null) throw new MissingMemberException("BSML GameplaySetup type not found");

            // 'Instance' is declared on a generic base class (ZenjectSingleton<T>), so it must be
            // looked up with FlattenHierarchy. It throws InvalidOperationException if the menu is too early.
            const BindingFlags staticFlags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            var instProp = gsType.GetProperty("Instance", staticFlags);
            var instField = instProp == null ? gsType.GetField("Instance", staticFlags) : null;
            if (instProp == null && instField == null)
                throw new MissingMemberException("GameplaySetup.Instance not found");

            object instance;
            try { instance = instProp != null ? instProp.GetValue(null) : instField.GetValue(null); }
            catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException)
            {
                throw e.InnerException;
            }
            if (instance == null) throw new InvalidOperationException("GameplaySetup.Instance is null (too early)");

            var methods = gsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .Where(m => m.Name == "AddTab")
                                .ToList();

            // Preferred: AddTab(string name, string resource, object host, MenuType type)
            var withType = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 4 && p[3].ParameterType.IsEnum;
            });

            const string name = "MPEnviornment";
            const string resource = "MultiplayerEnvironmentEditor.UI.lobby.bsml";

            if (withType != null)
            {
                var menuType = withType.GetParameters()[3].ParameterType;
                var online = Enum.GetNames(menuType).Contains("Online")
                    ? Enum.Parse(menuType, "Online")
                    : Enum.Parse(menuType, "All");
                withType.Invoke(instance, new object[] { name, resource, SettingsController.Instance, online });
                return;
            }

            // Fallback: AddTab(name, resource, host) - shows the tab in every menu type
            var threeArg = methods.FirstOrDefault(m => m.GetParameters().Length == 3)
                           ?? throw new MissingMethodException("No usable GameplaySetup.AddTab overload found");
            threeArg.Invoke(instance, new object[] { name, resource, SettingsController.Instance });
        }

        private static bool TryRegister(Action register, string what)
        {
            try
            {
                register();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // too early, try again next second
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"{what} failed: {ex}");
                return true; // don't retry forever on a real error
            }
        }
    }
}
