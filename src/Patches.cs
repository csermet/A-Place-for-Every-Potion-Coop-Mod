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
            // Back in the menu without a live co-op link: the client's next game is solo again.
            if (__0 is eGameState.Lobby && !(Coop.IsRunning && !Coop.IsHost))
                SavePaths.Deactivate();
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

    /// <summary>Our Co-op entry in the main menu is a copy of the Settings entry: its press opens the co-op window.</summary>
    [HarmonyPatch(typeof(Il2CppCodeBase.Components.UI.BaseButton), nameof(Il2CppCodeBase.Components.UI.BaseButton.UpImpl))]
    static class MenuButtonClickPatch
    {
        static void Postfix(Il2CppCodeBase.Components.UI.BaseButton __instance)
        {
            if (__instance != null && __instance.gameObject.name == UI.MenuButton.ObjectName)
                UI.CoopPanel.Toggle();
        }
    }

    /// <summary>The cat knocks bottles over at random; only the host's cat does, and the result is synced.</summary>
    [HarmonyPatch(typeof(CatBottlePusher), nameof(CatBottlePusher.Knock))]
    static class CatPatch
    {
        static bool Prefix() => !(Coop.IsConnected && !Coop.IsHost);
    }
}
