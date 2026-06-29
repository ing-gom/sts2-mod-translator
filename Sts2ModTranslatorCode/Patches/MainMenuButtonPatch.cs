using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2ModTranslator.Core;
using Sts2ModTranslator.Ui;

namespace Sts2ModTranslator.Patches;

// 메인 메뉴가 준비되면 'Mod Translator' 버튼 + 패널을 붙이고,
// 모드 로드가 끝난 이 시점에 번역을 한 번 더 주입한다(부팅 레이스 보정).
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class NMainMenuReadyPatch
{
    public static void Postfix(NMainMenu __instance)
    {
        TranslationSync.OnMainMenuReady(LocManager.Instance);
        TranslatorPanel.Attach(__instance);
    }
}
