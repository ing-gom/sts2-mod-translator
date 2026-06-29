using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2ModTranslator;

/// <summary>
/// STS2 Mod Translator — 다른 모드의 로컬라이즈 텍스트를 번역할 수 있게 해주는 도구 모드.
///
/// 동작 (Phase 1, 화면 패널 없음):
///   1. 부팅/언어전환 시 로드된 모드 중 `res://{id}/localization/<lang>/*.json` 을 동봉한 모드만 탐지.
///   2. 각 지원 모드의 eng 텍스트를 %APPDATA%\Sts2ModTranslator\ 아래로 추출(내용은 JSON, 확장자는 .txt):
///        source/{id}/eng/{table}.txt   (원문 참조, 읽기 전용)
///        overrides/{id}/{lang}/{table}.txt (번역 입력칸 — 값 비어 있음)
///   3. 지원/미지원 모드 목록 + 번역 진행률을 supported_mods.txt 로 출력.
///   4. LocManager.SetLanguage 직후 overrides 의 (비어 있지 않은) 값을 해당 로크 테이블에 MergeWith.
///
/// 모든 경로/열거가 게임 자신의 규약(GetModdedLocTables: res://{id}/localization/{lang}/{file})을
/// 그대로 사용하므로 추측이 없다. LocManager 를 우회해 텍스트를 하드코딩한 모드는 대상이 아니다.
/// </summary>
[ModInitializer(nameof(Initialize))]
public class MainFile
{
    public const string ModId = "Sts2ModTranslator";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; }
        = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        try
        {
            var harmony = new Harmony(ModId);
            harmony.PatchAll(typeof(MainFile).Assembly);
            Logger.Info($"[{ModId}] initialized — LocManager.SetLanguage hooked.");
        }
        catch (Exception ex)
        {
            Logger.Warn($"[{ModId}] init failed: {ex.Message}");
            Logger.Warn(ex.ToString());
        }
    }
}
