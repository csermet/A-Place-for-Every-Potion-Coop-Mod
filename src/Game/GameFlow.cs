using System;
using Il2Cpp;
using Il2CppCodeBase.Components.UI;
using Il2CppCodeBase.Services.Core;
using Il2CppCodeBase.Services.DataMediatorService;
using Il2CppCodeBase.Services.GameStateMachineService;
using Il2CppCodeBase.Services.PersistentDataService;
using Il2CppCodeBase.Services.PotionsPersistenceService;
using UnityEngine;

namespace PotionCoop.Game
{
    /// <summary>The game's services, and the moves between menu and lab that co-op makes on the player's behalf.</summary>
    internal static class GameFlow
    {
        const string MenuItems = "UISetup(Clone)/WindowsContainerCanvas/MainMenu(Clone)/Content/MenuItems/";

        public static IPersistentDataService Data => Resolve<IPersistentDataService>();
        public static IPotionsPersistence Persistence => Resolve<IPotionsPersistence>();
        public static IGameStateMachine Machine => Resolve<IGameStateMachine>();
        public static IDataMediator Mediator => Resolve<IDataMediator>();

        static T Resolve<T>() where T : class
        {
            try { return ServiceLocator.TryResolve<T>(out var service) ? service : null; }
            catch (Exception) { return null; }
        }

        public static bool InLobby => GameRefs.State == eGameState.Lobby;

        /// <summary>Host: from the main menu straight into the lab, the way the menu's own buttons do it.</summary>
        public static void HostEnterLab()
        {
            if (!InLobby)
                return;
            var lab = Mediator?.Lab;
            if (lab != null && lab.HasPlaythrough)
            {
                var continueStarter = GameObject.Find(MenuItems + "ContinueButton")?.GetComponent<AdvancedButtonContinueStarter>();
                if (continueStarter != null)
                {
                    continueStarter.AdvancedButton_OnUp(); // also asks about lab updates, like the button
                    return;
                }
            }
            var newGameStarter = GameObject.Find(MenuItems + "NewGameButton")?.GetComponent<AdvancedButtonNewGameStarter>();
            if (newGameStarter != null)
                newGameStarter.AdvancedButton_OnUp();
            else
                Machine?.Enter(eGameState.NewGame);
        }

        /// <summary>Guest: into the host's lab, which <see cref="Profiles.Enter"/> has just put in place.</summary>
        public static void GuestEnterLab()
        {
            if (!InLobby)
                return;
            Persistence?.ClaimDeal();
            Machine?.Enter(eGameState.ContinueGame);
        }

        /// <summary>Saves everything now (instead of a frame or two later).</summary>
        public static void SaveNow()
        {
            try { Persistence?.Flush(); }
            catch (Exception e) { CoopMod.Log.Warning($"saving the lab: {e.Message}"); }
            try { Data?.Save<PlayerProgress>(true); }
            catch (Exception e) { CoopMod.Log.Warning($"saving progress: {e.Message}"); }
        }

        /// <summary>Back to the main menu (saved first), as the pause menu's Quit to Menu does.</summary>
        public static void ToMainMenu()
        {
            if (GameRefs.State is eGameState.Lobby or eGameState.LoadLobby or eGameState.None)
                return;
            SaveNow();
            Machine?.Enter(eGameState.LoadLobby);
        }
    }
}
