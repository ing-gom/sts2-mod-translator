using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using Sts2ModTranslator.Ui;

namespace Sts2ModTranslator.Patches;

// 런 도중 일시정지 메뉴에 'Mod Translator' 항목 + 패널을 붙인다.
// 메인 메뉴 진입점(NMainMenuReadyPatch)과 같은 패널을 쓰며, 런을 나가지 않고도 줄을 고칠 수 있게 한다.
// 메뉴 인스턴스는 캐시돼 재사용되므로(NRunSubmenuStack._pauseMenu) 중복 부착은 노드 이름으로 막는다.
[HarmonyPatch(typeof(NPauseMenu), "_Ready")]
public static class NPauseMenuReadyPatch
{
    public static void Postfix(NPauseMenu __instance) => TranslatorPanel.AttachToPause(__instance);
}
