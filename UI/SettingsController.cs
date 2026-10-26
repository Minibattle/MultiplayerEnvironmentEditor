using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components.Settings;
using MultiplayerEnvironmentEditor.Configuration;

namespace MultiplayerEnvironmentEditor.UI
{
    internal class SettingsController
    {
        public static readonly SettingsController Instance = new SettingsController();

        // One per panel: the same controller fills in both the lobby tab and the settings menu,
        // so each needs its own reference to be refreshed.
        [UIComponent("spread-slider-lobby")]
        private SliderSetting _lobbySlider = null;

        [UIComponent("spread-slider-menu")]
        private SliderSetting _menuSlider = null;

        [UIValue("enabled")]
        public bool Enabled
        {
            get { return PluginConfig.Instance.Enabled; }
            set { PluginConfig.Instance.Enabled = value; }
        }

        [UIValue("spread")]
        public float Spread
        {
            get { return PluginConfig.Instance.OtherPlayerSpread; }
            set { PluginConfig.Instance.OtherPlayerSpread = value; }
        }

        [UIAction("reset-spread")]
        public void ResetSpread()
        {
            PluginConfig.Instance.OtherPlayerSpread = PluginConfig.DefaultSpread;

            // Pull the new value back into the controls so the handle actually moves.
            if (_lobbySlider != null) _lobbySlider.ReceiveValue();
            if (_menuSlider != null) _menuSlider.ReceiveValue();
        }

        [UIValue("hide-players")]
        public bool HidePlayers
        {
            get { return PluginConfig.Instance.HideOtherPlayers; }
            set { PluginConfig.Instance.HideOtherPlayers = value; }
        }

        [UIValue("hide-players-fully")]
        public bool HidePlayersFully
        {
            get { return PluginConfig.Instance.HidePlayersEntirely; }
            set { PluginConfig.Instance.HidePlayersEntirely = value; }
        }

        [UIValue("holograms")]
        public bool Holograms
        {
            get { return PluginConfig.Instance.ShowHolograms; }
            set { PluginConfig.Instance.ShowHolograms = value; }
        }

        [UIValue("vanilla-limits")]
        public bool VanillaLimits
        {
            get { return PluginConfig.Instance.VanillaPlayspaceLimits; }
            set { PluginConfig.Instance.VanillaPlayspaceLimits = value; }
        }

    }
}
