using System;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2ModTranslator.Core;

/// <summary>
/// SetLanguage postfix 가 호출하는 오케스트레이터.
///   - 첫 진입(모드 로드 완료 후)에 스캔 + 템플릿/원문 추출 + 리포트 출력 (언어별 1회).
///   - 매 호출마다 해당 언어의 번역 override 를 로크 테이블에 주입.
/// </summary>
public static class TranslationSync
{
    private static ScanResult? _scan;                       // 1회 스캔 캐시
    private static readonly HashSet<string> _prepedLangs = new(); // fs 준비 완료 언어

    /// <summary>인게임 패널용: 최근 스캔 결과(지원/미지원 모드).</summary>
    public static ScanResult? CurrentScan => _scan;

    /// <summary>현재 게임 언어 코드(예: kor). 없으면 eng.</summary>
    public static string CurrentLanguage()
    {
        try { return LocManager.Instance?.Language ?? "eng"; }
        catch { return "eng"; }
    }

    /// <summary>
    /// 디스크의 override 를 다시 읽어 현재 언어에 재주입 + 리포트 갱신(패널 'Reload' 버튼).
    /// 비어 있지 않은 값만 적용하므로 재시작 없이 편집분이 반영된다. 주입된 키 수 반환.
    /// </summary>
    public static int ReloadFromDisk()
    {
        var mgr = LocManager.Instance;
        if (mgr == null) return 0;
        _scan ??= ModLocScanner.Scan();
        string lang = mgr.Language;
        if (string.Equals(lang, "eng", StringComparison.OrdinalIgnoreCase)) return 0;
        TranslationStore.WriteReport(_scan, lang);
        return Inject(mgr, _scan, lang);
    }

    /// <summary>메인 메뉴 버튼 라벨 로크 키. "main_menu_ui" 테이블에 주입한다.</summary>
    public const string MenuLabelKey = "STS2MODTRANSLATOR-MENU";

    /// <summary>
    /// 우리 모드 자체 UI 문자열(메뉴 버튼 라벨 등)을 게임 로크 테이블에 주입.
    /// 언어 전환 시 테이블이 재생성되므로 매 SetLanguage 마다 다시 넣는다(영문 고정).
    /// </summary>
    public static void EnsureUiStrings(LocManager mgr)
    {
        if (mgr == null) return;
        try
        {
            mgr.GetTable("main_menu_ui").MergeWith(new Dictionary<string, string>
            {
                [MenuLabelKey] = "Mod Translator",
            });
        }
        catch { /* main_menu_ui 테이블 부재 등 — 라벨만 영향, 무시 */ }
    }

    public static void OnLanguageLoaded(LocManager locMgr, string language)
    {
        if (locMgr == null || string.IsNullOrEmpty(language)) return;

        EnsureUiStrings(locMgr);   // 메뉴 라벨은 모든 언어에서 필요

        // 모드가 아직 로드되지 않은 이른 SetLanguage 호출 → 스킵(이후 호출에서 처리됨).
        var scan = _scan;
        if (scan == null)
        {
            scan = ModLocScanner.Scan();
            if (scan.Supported.Count == 0 && scan.Unsupported.Count == 0)
                return; // 로드된 모드 없음 — 아직 이르다, 캐시하지 않음
            _scan = scan;
            MainFile.Logger.Info(
                $"[Sts2ModTranslator] scan: supported={scan.Supported.Count} unsupported={scan.Unsupported.Count}");
        }

        // eng 로 플레이 중이면 번역 대상 아님(원문). 템플릿/주입 모두 불필요.
        bool isSource = string.Equals(language, "eng", StringComparison.OrdinalIgnoreCase);

        // 언어별 1회: 원문 추출 + 번역 템플릿 생성 + 리포트.
        if (!isSource && !_prepedLangs.Contains(language))
        {
            foreach (var mod in scan.Supported)
            {
                try { TranslationStore.EnsureTemplates(mod, language); }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn($"[Sts2ModTranslator] 템플릿 생성 실패 {mod.Id}: {ex.Message}");
                }
            }
            TranslationStore.WriteReport(scan, language);
            _prepedLangs.Add(language);
            MainFile.Logger.Info(
                $"[Sts2ModTranslator] templates+report ready for '{language}' → {TranslationStore.Root}");
        }

        // 매 호출: 번역 주입.
        if (!isSource)
            Inject(locMgr, scan, language);
    }

    private static int Inject(LocManager locMgr, ScanResult scan, string language)
    {
        int mergedTables = 0, mergedKeys = 0;
        foreach (var mod in scan.Supported)
        {
            var overrides = TranslationStore.LoadNonEmptyOverrides(mod, language);
            foreach (var (table, dict) in overrides)
            {
                LocTable? lt = TryGetTable(locMgr, table);
                if (lt == null) continue; // 해당 테이블이 게임에 없음(미로드) — 스킵
                try
                {
                    lt.MergeWith(dict);
                    mergedTables++;
                    mergedKeys += dict.Count;
                }
                catch (Exception ex)
                {
                    MainFile.Logger.Warn(
                        $"[Sts2ModTranslator] merge 실패 {mod.Id}/{table}: {ex.Message}");
                }
            }
        }
        if (mergedKeys > 0)
            MainFile.Logger.Info(
                $"[Sts2ModTranslator] injected {mergedKeys} keys into {mergedTables} tables for '{language}'.");
        return mergedKeys;
    }

    private static LocTable? TryGetTable(LocManager locMgr, string table)
    {
        try { return locMgr.GetTable(table); }
        catch { return null; } // GetTable 은 미존재 시 LocException throw
    }
}
