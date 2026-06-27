using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using Sts2ModTranslator.Core;

namespace Sts2ModTranslator.Patches;

/// <summary>
/// 게임이 언어를 (재)로드할 때마다 호출되는 지점.
/// LoadTablesFromPath 가 베이스 + 모든 모드 텍스트를 merge 한 뒤 postfix 가 돌므로,
/// 여기서 우리 번역을 MergeWith 하면 항상 최종 승자가 된다.
///
/// Priority.Last: 다른 모드가 자기 SetLanguage postfix 로 텍스트를 주입하는 경우에도
/// 우리가 그보다 늦게 실행되도록 보장(런타임 주입형 모드 대비).
/// </summary>
[HarmonyPatch(typeof(LocManager), nameof(LocManager.SetLanguage))]
public static class LocManagerSetLanguagePatch
{
    [HarmonyPriority(Priority.Last)]
    public static void Postfix(LocManager __instance, string language)
    {
        try
        {
            TranslationSync.OnLanguageLoaded(__instance, language);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] sync 실패({language}): {ex.Message}");
        }
    }
}
