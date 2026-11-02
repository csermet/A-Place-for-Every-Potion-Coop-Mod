using HarmonyLib;
using Il2Cpp;
using Il2CppCodeBase.Components.Cat;
using Il2CppCodeBase.Components.Hero;
using Il2CppCodeBase.Components.Potions;
using Il2CppCodeBase.Components.Vats;
using Il2CppCodeBase.Components.Vessels;
using Il2CppCodeBase.Services.GameStateMachineService;
using Il2CppCodeBase.Services.PotionsPersistenceService;
using Il2CppCodeBase.Services.SaveLoadService;
using PotionCoop.Game;

namespace PotionCoop
{
    [HarmonyPatch(typeof(GameStateMachine), nameof(GameStateMachine.Enter), new[] { typeof(eGameState) })]
    static class GameStatePatch
    {
        static void Postfix(eGameState __0)
        {
            GameRefs.State = __0;
            if (__0 is eGameState.LoadLevel or eGameState.LoadLobby or eGameState.Lobby)
                Coop.OnLevelLeft();
            Coop.OnStateEntered(__0);
        }
    }

    /// <summary>
    /// The game stages each save in "&lt;save folder&gt;/.key.tmp", building that path itself. For the keys we
    /// redirect, the staged file must sit in the same (profile) folder as the file it replaces.
    /// </summary>
    [HarmonyPatch(typeof(SaveFileWrite), MethodType.Constructor, new[] { typeof(string), typeof(string) })]
    static class SaveTempPathPatch
    {
        static void Postfix(SaveFileWrite __instance, string __0)
        {
            if (SavePaths.Active)
                __instance._tempPath = SavePaths.Redirect(__0, __instance._tempPath);
        }
    }

    /// <summary>
    /// A guest connected to a host must never start a new game: it would reset the guest's progress and
    /// the host's world on this side. The menu's New Game then continues in the host's lab instead.
    /// </summary>
    [HarmonyPatch(typeof(Il2CppCodeBase.Components.UI.AdvancedButtonNewGameStarter), nameof(Il2CppCodeBase.Components.UI.AdvancedButtonNewGameStarter.AdvancedButton_OnUp))]
    static class GuestNewGamePatch
    {
        static bool Prefix()
        {
            if (!Coop.IsRunning || Coop.IsHost)
                return true;
            MainThread.Post(Coop.GuestProceed);
            return false;
        }
    }

    /// <summary>While the player types in a co-op text box, Back/Cancel (backspace, escape) edits the text, not the window.</summary>
    [HarmonyPatch(typeof(Il2CppCodeBase.Components.UI.UIWindowCloser), nameof(Il2CppCodeBase.Components.UI.UIWindowCloser.Cancel_Performed))]
    static class TypingCancelPatch
    {
        static bool Prefix() => !UI.TextBox.AnyFocused;
    }

    /// <summary>
    /// Likewise Continue: a connected guest must load the host's lab through its profile, never the solo lab
    /// (the host's states would be written into the solo save).
    /// </summary>
    [HarmonyPatch(typeof(Il2CppCodeBase.Components.UI.AdvancedButtonContinueStarter), nameof(Il2CppCodeBase.Components.UI.AdvancedButtonContinueStarter.AdvancedButton_OnUp))]
    static class GuestContinuePatch
    {
        static bool Prefix()
        {
            if (!Coop.IsRunning || Coop.IsHost)
                return true;
            MainThread.Post(Coop.GuestProceed);
            return false;
        }
    }

    /// <summary>Every change the game persists is a change the other player needs.</summary>
    [HarmonyPatch(typeof(PotionsPersistence))]
    static class PersistencePatches
    {
        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordHandRow))]
        static void HandRow() => Coop.MarkHeldRows();

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordApronRow))]
        static void ApronRow() => Coop.MarkHeldRows();

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordShelved))]
        static void Shelved(PotionFacade __0) => Coop.MarkPotion(__0);

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordDropped))]
        static void Dropped(PotionFacade __0) => Coop.MarkPotion(__0);

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordPlaced))]
        static void Placed(PotionFacade __0) => Coop.MarkPotion(__0);

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordHung))]
        static void Hung(PotionFacade __0) => Coop.MarkPotion(__0);

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.TryScoreShelve))]
        static void Scored(PotionFacade __0) => Coop.MarkPotion(__0);

        [HarmonyPostfix, HarmonyPatch(nameof(PotionsPersistence.RecordVat))]
        static void Vat(Il2CppCodeBase.Components.Vats.VatFacade __0) => Coop.MarkVat(__0);
    }

    /// <summary>
    /// Pours that finish without a placement record (into a shelved potion, into a vat, drawing from a tap):
    /// mark both ends so the new liquids go out at once instead of waiting for the periodic world check.
    /// </summary>
    [HarmonyPatch]
    static class PourPatches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(PotionFacade), nameof(PotionFacade.OnReceiveFinished))]
        static void PotionReceived(PotionFacade __instance, IPourStreamSource __0)
        {
            Coop.MarkPotion(__instance);
            MarkSource(__0);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(VatFacade), nameof(VatFacade.OnReceiveFinished))]
        static void VatReceived(VatFacade __instance, IPourStreamSource __0)
        {
            Coop.MarkVat(__instance);
            MarkSource(__0);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(VatFacade), nameof(VatFacade.OnTapDrawFinished))]
        static void TapDrawn(VatFacade __instance) => Coop.MarkVat(__instance);

        static void MarkSource(IPourStreamSource source)
        {
            var potion = source != null ? source.TryCast<PotionFacade>() : null;
            if (potion != null)
                Coop.MarkPotion(potion);
        }

        // A pour starting here: the other side plays it while it happens (the final states follow as usual).

        [HarmonyPostfix, HarmonyPatch(typeof(PotionFacade), nameof(PotionFacade.PourOutInto))]
        static void PourStarted(PotionFacade __instance, IPourVessel __0, eSectionFamily __1, int __2, bool __3)
        {
            if (__3 || !Coop.CanSendPour)
                return;
            var to = Pours.Ref(__0);
            if (to != null)
                Coop.SendPour(new Net.VesselRef { K = Net.VesselRef.Potion, U = __instance.Uid }, to, __1, __2);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(VatFacade), nameof(VatFacade.DrawOutInto))]
        static void DrawStarted(VatFacade __instance, VatTapFacade __0, PotionFacade __1, eSectionFamily __2, int __3, bool __4)
        {
            if (__4 || !Coop.CanSendPour || __0 == null || __1 == null)
                return;
            var tap = Pours.TapIndex(__instance, __0);
            if (tap < 0)
                return;
            Coop.SendPour(new Net.VesselRef { K = Net.VesselRef.Tap, U = __instance.Uid, I = tap },
                new Net.VesselRef { K = Net.VesselRef.Potion, U = __1.Uid }, __2, __3);
        }
    }

    /// <summary>Client: the shared world lives in its own save folder (see <see cref="SavePaths"/>).</summary>
    [HarmonyPatch(typeof(SaveLoadService))]
    static class SavePathPatches
    {
        [HarmonyPostfix, HarmonyPatch(nameof(SaveLoadService.KeyToFilePath))]
        static void KeyToFilePath(string __0, ref string __result) => __result = SavePaths.Redirect(__0, __result);

        [HarmonyPostfix, HarmonyPatch(nameof(SaveLoadService.TempFilePath))]
        static void TempFilePath(string __0, ref string __result) => __result = SavePaths.Redirect(__0, __result);

        [HarmonyPostfix, HarmonyPatch(nameof(SaveLoadService.BackupFilePath))]
        static void BackupFilePath(string __0, ref string __result) => __result = SavePaths.Redirect(__0, __result);
    }

    /// <summary>Undo could pull a bottle out of the other player's hands; off while connected.</summary>
    [HarmonyPatch(typeof(HeroMoveUndoer), nameof(HeroMoveUndoer.TryUndo))]
    static class UndoPatch
    {
        static bool Prefix(ref bool __result)
        {
            if (!Coop.IsConnected)
                return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Vat capacity comes from the owner's skills (Greater Vat). The vats are shared, so on the client
    /// they take the host's capacity; the friend's own skills and points stay untouched.
    /// </summary>
    [HarmonyPatch(typeof(VatConfigResolver), nameof(VatConfigResolver.Capacity), MethodType.Getter)]
    static class VatCapacityPatch
    {
        static void Postfix(VatConfigResolver __instance, ref int __result)
        {
            if (!Coop.IsConnected || Coop.IsHost || Coop.HostVatCapacity.Count == 0)
                return;
            var vat = __instance.GetComponent<VatFacade>();
            if (vat != null && Coop.HostVatCapacity.TryGetValue(vat.Uid, out var capacity))
                __result = capacity;
        }
    }

    [HarmonyPatch(typeof(VatFacade), nameof(VatFacade.Capacity), MethodType.Getter)]
    static class VatFacadeCapacityPatch
    {
        static void Postfix(VatFacade __instance, ref int __result)
        {
            if (!Coop.IsConnected || Coop.IsHost)
                return;
            if (Coop.HostVatCapacity.TryGetValue(__instance.Uid, out var capacity))
                __result = capacity;
        }
    }

    /// <summary>
    /// Our Co-op entry in the main menu is a copy of the Settings entry: its press opens the co-op window.
    /// A prefix, so the window opens even if one of the game's button modules (sounds…) fails afterwards.
    /// </summary>
    [HarmonyPatch(typeof(Il2CppCodeBase.Components.UI.BaseButton), nameof(Il2CppCodeBase.Components.UI.BaseButton.UpImpl))]
    static class MenuButtonClickPatch
    {
        static void Prefix(Il2CppCodeBase.Components.UI.BaseButton __instance)
        {
            try
            {
                if (__instance == null)
                    return;
                if (__instance.gameObject.name == UI.MenuButton.ObjectName)
                    MainThread.Post(UI.CoopPanel.Toggle);
                else
                    UI.UiClone.TryClick(__instance);
            }
            catch (System.Exception e) { CoopMod.Log.Error($"co-op click: {e}"); }
        }
    }

    /// <summary>The cat knocks bottles over at random; only the host's cat does, and the result is synced.</summary>
    [HarmonyPatch(typeof(CatBottlePusher), nameof(CatBottlePusher.Knock))]
    static class CatPatch
    {
        static bool Prefix() => !(Coop.IsConnected && !Coop.IsHost);
    }
}
