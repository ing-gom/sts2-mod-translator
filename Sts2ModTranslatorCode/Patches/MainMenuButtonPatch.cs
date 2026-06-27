using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using Sts2ModTranslator.Ui;

namespace Sts2ModTranslator.Patches;

// 메인 메뉴가 준비되면 'Mod Translator' 버튼 + 패널을 붙인다.
[HarmonyPatch(typeof(NMainMenu), "_Ready")]
public static class NMainMenuReadyPatch
{
    public static void Postfix(NMainMenu __instance) => TranslatorPanel.Attach(__instance);
}
