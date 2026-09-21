using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2ModTranslator.Core;

/// <summary>
/// SetLanguage postfix 가 호출하는 오케스트레이터.
///   - 첫 진입(모드 로드 완료 후)에 스캔 + 템플릿/원문 추출 + 리포트 출력 (언어별 1회).
///   - 매 호출마다 해당 언어의 번역 override 를 로크 테이블에 주입.
/// </summary>
public static class TranslationSync
{
    private static ScanResult? _scan;                       // 최신 스캔 캐시
    private static int _lastLoadedCount = -1;               // 마지막 스캔 시점의 로드된 모드 수
    private static readonly HashSet<string> _prepped = new(); // "lang\0modId" → fs 준비 완료

    /// <summary>인게임 패널용: 최근 스캔 결과(지원/미지원 모드).</summary>
    public static ScanResult? CurrentScan => _scan;

    /// <summary>현재 게임 언어 코드(예: kor). 없으면 eng.</summary>
    public static string CurrentLanguage()
    {
        try { return LocManager.Instance?.Language ?? "eng"; }
        catch { return "eng"; }
    }

    /// <summary>
    /// 게임이 지원하는 전체 언어 코드(14종, 선언 순서). 번역기는 현재 설정 언어뿐 아니라
    /// 이 전체 목록을 편집 대상으로 노출한다. API 실패 시 현재 언어 1종만 폴백.
    /// </summary>
    public static IReadOnlyList<string> SupportedLanguages()
    {
        try
        {
            var langs = LocManager.Languages;
            if (langs != null && langs.Count > 0) return langs;
        }
        catch { /* 정적 생성자 미초기화 등 — 폴백 */ }
        return new[] { CurrentLanguage() };
    }

    /// <summary>
    /// 디스크의 override 를 다시 읽어 현재 언어에 재주입 + 리포트 갱신(패널 'Reload' 버튼).
    /// 비어 있지 않은 값만 적용하므로 재시작 없이 편집분이 반영된다. 주입된 키 수 반환.
    /// </summary>
    public static int ReloadFromDisk()
    {
        LastInjectInvalidCount = 0; // 재주입 전 이전 카운트 초기화
        var mgr = LocManager.Instance;
        if (mgr == null) return 0;
        var scan = EnsureScan();
        if (scan == null) return 0;
        string lang = mgr.Language;
        // eng 도 스킵하지 않는다 — eng 폴더에 비영어 원문을 담은 모드는 eng 번역을 재주입해야 한다.
        // 자기 원문 언어인 모드는 Inject 안에서 ContentLang 기준으로 걸러진다.
        TranslationStore.WriteReport(scan, lang);
        int n = Inject(mgr, scan, lang);
        RefreshLabels(mgr); // 이미 렌더된 라벨(메인메뉴 등)도 즉시 다시 읽게 통지
        return n;
    }

    /// <summary>
    /// 이미 렌더된 라벨/버튼을 즉시 다시 읽게 한다.
    /// 게임의 LocTextLabel/NButton 등은 Godot 번역변경 알림(NOTIFICATION_TRANSLATION_CHANGED=2010)으로
    /// 재로컬라이즈한다. LocManager.TriggerLocaleChange 는 같은 locale 이면 SetLocale 이 no-op 이라 알림이
    /// 안 뜨므로, 트리 전체에 알림 2010 을 직접 전파한다.
    /// </summary>
    private static void RefreshLabels(LocManager mgr)
    {
        try
        {
            if (Godot.Engine.GetMainLoop() is Godot.SceneTree tree && tree.Root != null)
                tree.Root.PropagateNotification(2010); // NOTIFICATION_TRANSLATION_CHANGED
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] locale refresh 실패: {ex.Message}");
        }
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

        // 모드가 아직 로드되지 않은 이른 SetLanguage 호출 → null(이후 호출에서 처리됨).
        var scan = EnsureScan();
        if (scan == null) return;

        // 전역 eng 스킵을 두지 않는다 — eng 폴더에 비영어 원문(예: 한국어)을 담은 모드는 eng 로
        // 플레이할 때 오히려 번역(영어 override)을 주입해야 한다. "자기 원문 언어면 스킵" 은
        // EnsureTemplates/Inject 안에서 모드별 ContentLang 기준으로 처리한다.
        EnsureTemplates(scan, language); // 신규(또는 늦게 로드된) 모드만 증분 준비
        Inject(locMgr, scan, language);
    }

    /// <summary>
    /// 메인 메뉴 준비 시점(= 모드 로드 완료가 보장되는 늦은 지점)에서 한 번 더 스캔+주입.
    /// 부팅 때 SetLanguage 가 모드 로드보다 먼저 불려 번역이 누락되던 레이스를 보정한다.
    /// 부팅 SetLanguage 가 단 한 번뿐인 환경(사용자가 언어를 바꾸지 않음)도 여기서 구제된다.
    /// </summary>
    public static void OnMainMenuReady(LocManager locMgr)
    {
        if (locMgr == null) return;
        try
        {
            var scan = EnsureScan();
            if (scan == null) return;
            string lang = locMgr.Language;
            if (string.IsNullOrEmpty(lang)) return;
            // eng 도 스킵하지 않는다 — 모드별 ContentLang 게이팅이 EnsureTemplates/Inject 안에서 처리.
            EnsureTemplates(scan, lang);
            Inject(locMgr, scan, lang);
            RefreshLabels(locMgr); // 이미 그려진 메인메뉴 라벨도 즉시 재로컬라이즈
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] main-menu refresh 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 로드된 모드를 (재)스캔. 첫 SetLanguage 시점에 아직 로드되지 않았던 모드가 있어도
    /// 로드된 모드 수가 바뀌면 다시 스캔해 이후에 합류시킨다(부분 스캔을 영구 캐시하던 레이스 수정).
    /// 반환: 지원/미지원 합쳐 1개 이상이면 결과, 아무 모드도 없으면 기존 캐시(없으면 null).
    /// </summary>
    private static ScanResult? EnsureScan()
    {
        int loaded;
        try { loaded = ModManager.GetLoadedMods().Count(); }
        catch { loaded = -1; }

        // 캐시가 있고 로드된 모드 수에 변화가 없으면 재스캔 불필요.
        if (_scan != null && loaded == _lastLoadedCount) return _scan;

        var scan = ModLocScanner.Scan();
        if (scan.Supported.Count == 0 && scan.Unsupported.Count == 0)
            return _scan; // 아직 로드된 모드 없음 — 기존 캐시 유지(없으면 null)

        _scan = scan;
        _lastLoadedCount = loaded;
        MainFile.Logger.Info(
            $"[Sts2ModTranslator] scan: supported={scan.Supported.Count} unsupported={scan.Unsupported.Count}");
        return _scan;
    }

    /// <summary>
    /// 아직 준비되지 않은 (언어, 모드) 조합에 대해서만 원문 추출 + 번역 템플릿 생성.
    /// 늦게 로드돼 새로 합류한 모드도 빠짐없이 준비된다. 신규 준비가 있으면 리포트도 갱신.
    /// </summary>
    private static void EnsureTemplates(ScanResult scan, string language)
    {
        bool wrote = false;
        foreach (var mod in scan.Supported)
        {
            // 이 모드의 실제 원문 언어로 플레이 중이면 번역 대상 아님(자기 원문) — 템플릿 불필요.
            if (string.Equals(language, mod.ContentLang, StringComparison.OrdinalIgnoreCase)) continue;
            string key = language + "\0" + mod.Id;
            if (!_prepped.Add(key)) continue; // 이미 준비됨
            try { TranslationStore.EnsureTemplates(mod, language); wrote = true; }
            catch (Exception ex)
            {
                _prepped.Remove(key); // 실패 → 다음 호출에서 재시도
                MainFile.Logger.Warn($"[Sts2ModTranslator] 템플릿 생성 실패 {mod.Id}: {ex.Message}");
            }
        }
        if (wrote)
        {
            TranslationStore.WriteReport(scan, language);
            // AI 킷은 리포트와 한 쌍 — 스킬이 그 리포트를 작업목록으로 참조한다. _prepped 가 인메모리라
            // 이 블록은 부팅마다 한 번 실행되므로, 워크샵 업데이트로 폴더가 갈아엎혀도 여기서 복구된다.
            AiKitWriter.Write(language);
            MainFile.Logger.Info(
                $"[Sts2ModTranslator] templates+report ready for '{language}' → {TranslationStore.Root}");
        }
    }

    // ── SmartFormat 검증 게이트 ─────────────────────────────────

    /// <summary>직전 주입에서 걸러낸(문법 깨진) 항목 수. 패널 상태줄 표시용.</summary>
    public static int LastInjectInvalidCount { get; private set; }

    private const int MaxInvalidLogPerTable = 10;

    /// <summary>
    /// LocValidator 안전 래퍼. SmartFormat 파서는 일부 기형 문자열(예: "{X:diff(" — solo-verify
    /// 실측)에서 ParsingErrors 대신 IndexOutOfRangeException 을 던진다. 그런 문자열은 런타임
    /// 렌더링에서도 게임의 catch(FormattingException|ParsingErrors) 를 뚫고 나가므로(크래시 경로)
    /// 예외 = 무효로 판정해 반드시 걸러낸다.
    /// </summary>
    internal static bool TryValidateFormat(string text, out string? error)
    {
        try { return LocValidator.ValidateFormatString(text, out error); }
        catch (Exception ex)
        {
            error = $"format parser crashed ({ex.GetType().Name}) — invalid";
            return false;
        }
    }

    /// <summary>
    /// 주입 직전 SmartFormat 문법 검증. 게임 자체 localization_override 로더는 LocValidator 로
    /// 깨진 항목을 걸러 적용하지 않지만, MergeWith 런타임 주입은 그 검증을 우회한다.
    /// 깨진 포맷 문자열은 카드 설명이 그려질 때마다(파일 이동/타겟팅 호버/co-op 카드 인텐트)
    /// SmartFormat 예외 → StackTrace 생성 + 동기 로그 + Sentry 캡처를 반복시켜 전투 중 지속
    /// 스터터를 유발한다. 여기서 게임과 동일한 검증을 적용해 유효 항목만 통과시킨다.
    /// dict 는 SimpleLocCompat.ApplyAll 이 만든 새 dict 만 받는다(제자리 제거 안전).
    /// </summary>
    private static Dictionary<string, string> FilterValidFormats(Dictionary<string, string> dict, string where)
    {
        List<string>? bad = null;
        foreach (var kv in dict)
        {
            if (TryValidateFormat(kv.Value, out string? err)) continue;
            bad ??= new List<string>();
            bad.Add(kv.Key);
            if (bad.Count <= MaxInvalidLogPerTable)
                MainFile.Logger.Warn(
                    $"[Sts2ModTranslator] invalid format — not applied: {where}/{kv.Key}: {err}");
        }
        if (bad == null) return dict; // 전부 유효(일반 경로) — 추가 비용 없음
        if (bad.Count > MaxInvalidLogPerTable)
            MainFile.Logger.Warn(
                $"[Sts2ModTranslator] …and {bad.Count - MaxInvalidLogPerTable} more invalid entries in {where}.");
        LastInjectInvalidCount += bad.Count;
        foreach (var k in bad) dict.Remove(k);
        return dict;
    }

    /// <summary>
    /// override JSON 텍스트에서 SmartFormat 문법이 깨진 (비어 있지 않은) 값의 키 목록.
    /// 편집기 Save/파일 목록의 즉시 경고용 — 깨진 중괄호는 JSON 으로는 유효해서
    /// 기존 JSON 오류 경고에 잡히지 않는다. 주입 게이트와 동일하게 SimpleLoc 변환 후 검증.
    /// JSON 자체가 깨진 텍스트는 빈 목록(기존 JSON 오류 경고가 담당).
    /// </summary>
    public static List<string> InvalidFormatKeys(string json)
    {
        var bad = new List<string>();
        Dictionary<string, string>? d;
        try { d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json, LocJson.Read); }
        catch { return bad; }
        if (d == null) return bad;
        foreach (var kv in d)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (!TryValidateFormat(SimpleLocCompat.Apply(kv.Value), out _))
                bad.Add(kv.Key);
        }
        return bad;
    }

    // ── 주입 장부(우리가 덮어쓴 키의 '덮기 직전 값') ────

    /// <summary>
    /// 우리가 로크 테이블에 덮어쓴 키의 <b>덮기 직전 값</b>. "모드id\0테이블" → 키 → 원래 값
    /// (그 키가 테이블에도 폴백에도 없었으면 null = 우리가 새로 만든 키).
    ///
    /// 왜 필요한가: <see cref="LocTable.MergeWith"/> 는 제거를 못 한다. 예전엔 "모든 키를 eng 기본값까지
    /// 포함해 매번 다시 써 넣는" 방식으로 '번역을 비우면 원문 복귀' 를 구현했는데, 그 방식은 우리가 모르는
    /// 경로로 테이블에 들어온 현지화(게임 자체 user://localization_override, 런타임 머지, 파싱 실패한 파일)를
    /// 통째로 eng 원문으로 덮어써 지웠다. 이제는 <b>번역이 있는 키만</b> 주입하고, 번역이 사라진 키는
    /// 이 장부에 적어 둔 원래 값으로 되돌린다.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string?>> _touched =
        new(StringComparer.Ordinal);

    /// <summary>장부가 기록된 언어. 언어가 바뀌면 게임이 테이블을 새로 만드므로 장부도 버린다.</summary>
    private static string? _touchedLang;

    private static void ResetLedgerIfLanguageChanged(string language)
    {
        if (string.Equals(_touchedLang, language, StringComparison.Ordinal)) return;
        _touched.Clear();
        _touchedLang = language;
    }

    /// <summary>테이블의 현재 값(폴백 포함). 없으면 null.</summary>
    private static string? CurrentValue(LocTable lt, string key)
    {
        try { return lt.HasEntry(key) ? lt.GetRawText(key) : null; }
        catch { return null; } // LocException 등 — '없음' 으로 취급
    }

    /// <summary>
    /// 한 (모드, 테이블)의 주입을 적용한다. 순서:
    ///   1) SimpleLoc 문법 변환 + SmartFormat 검증(깨진 항목 탈락)
    ///   2) 이번에 주입하지 않는데 <b>예전에 우리가 덮었던</b> 키를 원래 값으로 복원
    ///      (원래 없던 키는 MergeWith 로 지울 수 없으므로 장부에 남겨 두고 그대로 둔다)
    ///   3) 덮기 직전 값을 장부에 기록(최초 1회)한 뒤 병합
    /// 반환: 실제로 주입한 키 수.
    /// </summary>
    private static int ApplyToTable(LocTable lt, string modId, string table, Dictionary<string, string> desired)
    {
        string where = $"{modId}/{table}";
        var valid = FilterValidFormats(SimpleLocCompat.ApplyAll(desired), where);
        string ledgerKey = modId + "\0" + table;

        try
        {
            if (_touched.TryGetValue(ledgerKey, out var ledger))
            {
                Dictionary<string, string>? restore = null;
                foreach (var kv in ledger)
                {
                    if (valid.ContainsKey(kv.Key)) continue; // 계속 주입 중 — 복원 대상 아님
                    if (kv.Value == null) continue;          // 우리가 만든 키 — 지울 수 없어 그대로 둔다
                    (restore ??= new Dictionary<string, string>(StringComparer.Ordinal))[kv.Key] = kv.Value;
                }
                if (restore != null)
                {
                    lt.MergeWith(restore);
                    foreach (var k in restore.Keys) ledger.Remove(k); // 원상복구 완료 — 장부에서 제거
                    MainFile.Logger.Info(
                        $"[Sts2ModTranslator] restored {restore.Count} original entr(ies) in {where}.");
                }
            }

            if (valid.Count == 0) return 0;

            if (!_touched.TryGetValue(ledgerKey, out var led))
                _touched[ledgerKey] = led = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var key in valid.Keys)
                if (!led.ContainsKey(key)) led[key] = CurrentValue(lt, key); // 최초 1회만 스냅샷

            lt.MergeWith(valid);
            return valid.Count;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] merge 실패 {where}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>이 (모드, 테이블)에 우리가 예전에 덮어쓴 키가 남아 있는지 — 복원만을 위한 방문이 필요한지 판정.</summary>
    private static bool HasLedger(string modId, string table) =>
        _touched.TryGetValue(modId + "\0" + table, out var d) && d.Count > 0;

    private static int Inject(LocManager locMgr, ScanResult scan, string language)
    {
        LastInjectInvalidCount = 0;
        ResetLedgerIfLanguageChanged(language); // 언어가 바뀌면 게임이 테이블을 새로 만든다 — 장부 폐기
        int translated = 0;
        var supportedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in scan.Supported)
        {
            supportedIds.Add(mod.Id); // 게이트보다 먼저 — bundled 루프가 중복 주입하지 않게.
            // 이 모드의 실제 원문 언어로 플레이 중이면 기본은 원문 그대로가 정답 — 전체 재주입은 안 한다.
            // 단, 사용자가 원문 텍스트를 의도적으로 고쳐 넣은 override(비어 있지 않은 값)만 골라 덮어쓴다.
            // 아무 것도 고치지 않았으면 아무 일도 안 일어난다(원문 그대로 = 기존 동작과 동일).
            // (한국어를 eng/ 에 담은 모드는 eng≠ContentLang 이라 아래 일반 경로로 정상 진행된다.)
            if (string.Equals(language, mod.ContentLang, StringComparison.OrdinalIgnoreCase))
            {
                translated += InjectOriginalOverrides(locMgr, mod, language);
                continue;
            }
            // 이 대상 모드에 설치된 번역 모드가 제공한 (언어별) 번역.
            var bundledForMod = scan.Bundled.ForTargetLang(mod.Id, language);

            // eng 테이블 ∪ 번역 모드가 제공한 테이블. 각 테이블에 <b>번역이 있는 키만</b> 주입한다.
            // 번역이 없는 키는 건드리지 않는다 — 원문은 게임이 이미 들고 있고(모드 동봉 파일/게임 자체
            // localization_override/런타임 머지), 우리가 eng 기본값으로 다시 써 넣으면 그걸 지워 버린다.
            // 번역을 비웠을 때의 복귀는 ApplyToTable 의 장부(덮기 직전 값)가 처리한다.
            var tables = new HashSet<string>(mod.EngByTable.Keys, StringComparer.Ordinal);
            if (bundledForMod != null) foreach (var t in bundledForMod.Keys) tables.Add(t);

            foreach (var table in tables)
            {
                var bundledTbl = bundledForMod != null && bundledForMod.TryGetValue(table, out var bt) ? bt : null;
                var dict = TranslationStore.BuildInjectTable(mod, language, table, bundledTbl);
                // 주입할 것도 없고 예전에 덮은 것도 없으면 테이블을 열 이유가 없다.
                if (dict.Count == 0 && !HasLedger(mod.Id, table)) continue;
                LocTable? lt = TryGetTable(locMgr, table);
                if (lt == null) continue; // 게임에 없는 테이블 — 스킵
                // 주입 전 BaseLib SimpleLoc 저작 문법(#, !Var!, *gold*, [E] 등)을 STS2 네이티브로 변환하고
                // (BaseLib 미사용 모드/일반 값은 그대로 통과 — 무해), SmartFormat 검증을 통과한 항목만 넣는다.
                ApplyToTable(lt, mod.Id, table, dict);
            }
            translated += TranslationStore.Coverage(mod, language).translated;
        }

        // 스캔에서 "지원" 으로 잡히지 않은 대상(localization 폴더 없이 런타임에 로크 테이블을
        // 등록하는 모드 등)도 번역 모드가 번역했다면 직접 주입한다(원문 기준이 없으므로 그대로 merge).
        foreach (var (targetId, byLang) in scan.Bundled.ByTarget)
        {
            if (supportedIds.Contains(targetId)) continue;
            if (!byLang.TryGetValue(language, out var byTable)) continue;
            foreach (var (table, dict) in byTable)
            {
                if (dict.Count == 0 && !HasLedger(targetId, table)) continue;
                LocTable? lt = TryGetTable(locMgr, table);
                if (lt == null) continue;
                // 번역 팩(bundled)도 동일하게 SimpleLoc 문법 변환 + 검증 + 장부 기록 후 주입.
                ApplyToTable(lt, targetId, table,
                    new Dictionary<string, string>(dict, StringComparer.Ordinal));
            }
        }

        MainFile.Logger.Info(
            $"[Sts2ModTranslator] applied translations for '{language}': {translated} active overrides"
            + (scan.Bundled.Any ? $", {scan.Bundled.Providers.Count} translation pack(s)" : "") + ".");
        return translated;
    }

    /// <summary>
    /// 원문 언어로 플레이 중인 모드에, 사용자가 원문 위에 덮어쓴 <b>비어 있지 않은 override</b>만 주입한다.
    /// 전체 테이블을 defaults 로 재구성하지 않으므로, 고치지 않은 항목은 게임의 원문 텍스트가 그대로 남는다.
    /// (원문 텍스트를 리워딩/오타수정하려는 의도적 편집을 존중 — 아무 것도 안 고쳤으면 no-op.)
    /// 주입한 키 수 반환.
    /// </summary>
    internal static int InjectOriginalOverrides(LocManager locMgr, SupportedMod mod, string language)
    {
        int n = 0;
        ResetLedgerIfLanguageChanged(language);
        foreach (var table in mod.EngByTable.Keys)
        {
            var dict = TranslationStore.LoadNonEmptyOverrides(mod.Id, language, table);
            if (dict.Count == 0 && !HasLedger(mod.Id, table)) continue;
            LocTable? lt = TryGetTable(locMgr, table);
            if (lt == null) continue;
            // 일반 주입과 동일하게 SimpleLoc 문법 변환 + SmartFormat 검증 + 장부 기록 후 병합
            // (사용자가 원문 위 편집을 지우면 장부에 적힐 원래 원문으로 되돌아온다).
            n += ApplyToTable(lt, mod.Id, table, dict);
        }
        return n;
    }

    private static LocTable? TryGetTable(LocManager locMgr, string table)
    {
        try { return locMgr.GetTable(table); }
        catch { return null; } // GetTable 은 미존재 시 LocException throw
    }
}
