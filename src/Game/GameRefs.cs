using Il2Cpp;
using Il2CppCodeBase.Components.Hero;
using Il2CppCodeBase.Components.Lab;
using Il2CppCodeBase.Services.PotionsPersistenceService;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PotionCoop.Game
{
    /// <summary>Cached handles to the game objects the mod talks to. Re-resolved after each level load.</summary>
    internal static class GameRefs
    {
        static LabPotionsPopulator _populator;
        static HeroFacade _hero;

        /// <summary>Tracked by a patch on GameStateMachine.Enter.</summary>
        public static eGameState State { get; set; } = eGameState.None;

        public static LabPotionsPopulator Populator
        {
            get
            {
                if (_populator == null)
                    _populator = Object.FindFirstObjectByType<LabPotionsPopulator>();
                return _populator;
            }
        }

        public static HeroFacade Hero
        {
            get
            {
                if (_hero == null)
                    _hero = Object.FindFirstObjectByType<HeroFacade>();
                return _hero;
            }
        }

        public static IPotionsPersistence Persistence => Populator?._potionsPersistence;

        /// <summary>The parent the game itself gives to dropped bottles (the populator's floor root is not set at runtime).</summary>
        public static Transform DropRoot
        {
            get
            {
                if (_dropRootResolved)
                    return _dropRoot;
                var picker = Hero != null ? Hero._picker : null;
                _dropRoot = picker != null ? picker._droppedPotionsRoot : null;
                if (_dropRoot == null && Populator != null)
                    _dropRoot = Populator._floorPotionsRoot;
                _dropRootResolved = true;
                CoopMod.Log.Msg($"drop root: {(_dropRoot != null ? _dropRoot.name : "none (moving transforms directly)")}");
                return _dropRoot;
            }
        }

        static Transform _dropRoot;
        static bool _dropRootResolved;

        /// <summary>The lab is loaded and its vessels are in place, so world state can be read and applied.</summary>
        public static bool InLevel =>
            State is eGameState.Playing or eGameState.Paused or eGameState.Skills or eGameState.PourModeQuestion
                or eGameState.AccessibilityTip or eGameState.IntroLetter
            && Populator != null && Populator.IsPopulated && Hero != null;

        public static void Forget()
        {
            _populator = null;
            _hero = null;
            _dropRoot = null;
            _dropRootResolved = false;
        }
    }
}
