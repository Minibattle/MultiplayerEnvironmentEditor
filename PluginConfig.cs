using System.Runtime.CompilerServices;
using IPA.Config.Stores;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]
namespace MultiplayerEnvironmentEditor.Configuration
{
    internal class PluginConfig
    {
        public static PluginConfig Instance { get; set; }

        /// <summary>What the spacing slider's reset button goes back to.</summary>
        public const float DefaultSpread = 2.5f;

        // BSIPA generates the implementation, so properties must be virtual.
        public virtual bool Enabled { get; set; } = true;

        /// <summary>
        /// Use the singleplayer HUD instead of the multiplayer one, so the two don't overlap.
        /// </summary>
        public virtual bool UseSoloHud { get; set; } = true;

        /// <summary>
        /// Child paths on the local player to switch off, because the singleplayer environment
        /// provides its own version of them. Comma-separated.
        /// </summary>
        public virtual string HideLocalPlayerObjects { get; set; } =
            "IsActiveObjects/Lasers,IsActiveObjects/Construction,IsActiveObjects/BigSmokePS," +
            "IsActiveObjects/DustPS,IsActiveObjects/DirectionalLights";

        /// <summary>
        /// How far out to the sides the other players sit, as a multiplier. 1 is the game's own
        /// spacing; higher pushes each player further toward their own side.
        /// </summary>
        public virtual float OtherPlayerSpread { get; set; } = DefaultSpread;

        /// <summary>
        /// Metres between neighbouring players when the lobby is lined up side by side.
        /// The spacing slider scales this.
        /// </summary>
        public virtual float SideBySideSpacing { get; set; } = 2.5f;

        /// <summary>Stop the other players' notes and walls being drawn.</summary>
        public virtual bool HideOtherPlayers { get; set; } = false;

        /// <summary>Make the other players and their platforms completely invisible.</summary>
        public virtual bool HidePlayersEntirely { get; set; } = false;

        /// <summary>
        /// Map requirements that hide the other players' notes and walls automatically, because
        /// these mods take over the play space. Comma-separated; spacing and case are ignored when matching.
        /// </summary>
        public virtual string AutoHideForRequirements { get; set; } =
            "Vivify,Noodle Extensions,Mapping Extensions";

        /// <summary>Show the first-place player's hologram.</summary>
        public virtual bool ShowHolograms { get; set; } = true;

        /// <summary>
        /// Map requirements that hide the hologram automatically, because these mods take over
        /// the visuals. Comma-separated; spacing and case are ignored when matching.
        /// </summary>
        public virtual string AutoHideHologramForRequirements { get; set; } = "Vivify";

        /// <summary>
        /// Keep the game's normal limits on how far a player may move from their platform.
        /// Off by default, which lets players roam and lets arms stretch.
        /// </summary>
        public virtual bool VanillaPlayspaceLimits { get; set; } = false;

        /// <summary>
        /// Keep playing when something interrupts the song: leave the notes and walls on screen,
        /// and don't treat the SteamVR overlay taking focus as giving up.
        /// </summary>
        public virtual bool KeepPlayingWhenInterrupted { get; set; } = true;

        /// <summary>Step-by-step logging while setting a song up. Off unless you are debugging.</summary>
        public virtual bool Diagnostics { get; set; } = false;

        public virtual void OnReload() { }
        public virtual void Changed() { }

        public virtual void CopyFrom(PluginConfig other)
        {
            Enabled = other.Enabled;
            UseSoloHud = other.UseSoloHud;
            HideLocalPlayerObjects = other.HideLocalPlayerObjects;
            OtherPlayerSpread = other.OtherPlayerSpread;
            SideBySideSpacing = other.SideBySideSpacing;
            HideOtherPlayers = other.HideOtherPlayers;
            HidePlayersEntirely = other.HidePlayersEntirely;
            AutoHideForRequirements = other.AutoHideForRequirements;
            ShowHolograms = other.ShowHolograms;
            AutoHideHologramForRequirements = other.AutoHideHologramForRequirements;
            VanillaPlayspaceLimits = other.VanillaPlayspaceLimits;
            KeepPlayingWhenInterrupted = other.KeepPlayingWhenInterrupted;
            Diagnostics = other.Diagnostics;
        }
    }
}
