using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2ModTranslator.Core;

/// <summary>
/// DeepL 자동 번역 보조기. 편집기의 *빈* 값만 기계번역으로 채워 "초안"을 만든다(자동 저장 안 함 —
/// 사용자가 검수 후 Save). 원문(eng) 키 중 현재 언어 override 가 비어 있는 것만 대상으로 한다.
///
/// 토큰 보존: 게임 텍스트의 서식은 두 종류로 나눠 처리한다.
///  · 단독 플레이스홀더(<c>{0}</c>, 홀 태그)와 데이터 짝 태그(<c>[img]경로[/img]</c>)는 <c>&lt;ph&gt;…&lt;/ph&gt;</c>(ignore_tags)로 통째 보존.
///  · 텍스트 짝 태그(<c>[sine]…[/sine]</c>, <c>[color=x]…[/color]</c>, <c>&lt;b&gt;…&lt;/b&gt;</c>)는 <c>&lt;gN&gt;…&lt;/gN&gt;</c>
///    진짜 XML 요소로 바꿔 안쪽만 번역시키고 태그는 그 내용에 계속 붙게 한다(어순이 바뀌어도 태그가 단어를 감싼 채 이동).
/// 응답에서 <c>&lt;ph&gt;</c>/<c>&lt;gN&gt;</c> 래퍼를 원문 마크업으로 복원한다. → 플레이스홀더 파손·태그 이탈을 막는다.
///
/// API 키는 로컬(<see cref="TranslationStore"/> 루트)에만 저장되며, 내보내는 번역 모드에는 포함되지 않는다.
/// 외부 통신은 이 보조기를 누를 때만 발생한다(opt-in).
/// </summary>
public static class AutoTranslator
{
    // 단독 토큰: [..], <..>. 짝 없는 홀 태그 → 번역 제외(원형 보존).
    // ({..} 변수는 중첩({InCombat:..{CombatHeal}..})이 있어 정규식 대신 균형 스캐너로 통째 처리.)
    private static readonly Regex StandaloneRx =
        new(@"\G(\[[^\[\]]*\]|<[^<>]*>)", RegexOptions.Compiled);

    // !변수! 플레이스홀더(일부 모드: !ChargeCost!, !D!). CamelCase/숫자/_ 만 — 문장 속 느낌표("Wow!")
    // 오탐을 피하려 보수적으로: '!' 뒤 곧바로 영문자 + 단어문자 + '!'. 통째 보존(<ph>).
    private static readonly Regex VarBangRx =
        new(@"\G![A-Za-z][A-Za-z0-9_]*!", RegexOptions.Compiled);

    // 안쪽이 '데이터'(이미지 경로 등)라 통째로 보존해야 하는 짝 태그: [img]path[/img] 등.
    // 짝 태그지만 안쪽을 번역시키면 안 되므로 전체를 하나의 단독 토큰으로 취급한다.
    private static readonly Regex OpaquePairedRx =
        new(@"\G\[(img|sprite|icon)\](.*?)\[/\1\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    // 짝 BBCode 태그: [tag]..[/tag] / [tag=val]..[/tag] (같은 이름). 안쪽은 번역 대상.
    private static readonly Regex PairedBbRx =
        new(@"\G\[([A-Za-z_][\w-]*)(=[^\]\r\n]*)?\](.*?)\[/\1\]",
            RegexOptions.Compiled | RegexOptions.Singleline);

    // 짝 각괄호 태그: <tag ...>..</tag> (같은 이름). 안쪽은 번역 대상.
    private static readonly Regex PairedAngleRx =
        new(@"\G<([A-Za-z_][\w-]*)([^>]*)>(.*?)</\1>",
            RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    /// <summary>DeepL 키 발급 안내 페이지. 키 입력 모달과 오류 안내가 같은 주소를 쓴다.</summary>
    public const string SignupUrl = "https://www.deepl.com/pro-api";

    /// <summary>
    /// 지역 차단 안내의 공통 꼬리말. DeepL 은 러시아·벨라루스 등 일부 지역에 서비스하지 않아
    /// 가입도 API 호출도 막힌다 — <b>키가 멀쩡해도</b> 실패하므로 "키를 확인하라"고만 하면 오진이 된다.
    /// </summary>
    public const string RegionNote =
        "DeepL doesn't serve every region (Russia and Belarus among them) — there it only works over a VPN. "
        + "Without one, use \"Translate with AI…\" instead: that route never contacts DeepL.";

    // 오류 메시지 앞부분에 심는 표식. UI 가 "키 문제가 아닐 수 있다"는 후속 안내를 띄울지 판단한다.
    private const string UnreachableTag = "Couldn't reach DeepL";
    private const string RefusedTag = "DeepL refused the request";

    /// <summary>
    /// 이 오류가 키 문제가 아니라 <b>네트워크/지역 차단</b>일 수 있는지. 도달 실패(DNS·타임아웃)와
    /// HTTP 403 이 해당한다 — 403 은 잘못된 키일 수도, 차단된 지역일 수도 있어 둘 다 안내한다.
    /// </summary>
    public static bool LooksBlocked(string error) =>
        error.Contains(UnreachableTag, StringComparison.Ordinal)
        || error.Contains(RefusedTag, StringComparison.Ordinal);

    // DeepL 요청당 보수적 상한(텍스트 개수 / 합산 바이트). 문서 한도(50개·128KiB)보다 낮게.
    private const int MaxBatchCount = 40;
    private const int MaxBatchChars = 90_000;

    /// <summary>
    /// STS 언어 코드 → DeepL target_lang. 매핑 없으면 null(호출부가 안내).
    /// 게임 언어 드롭다운(NLanguageDropdown)의 30개 코드를 전부 덮는다.
    /// </summary>
    public static string? DeepLTarget(string stsLang) => (stsLang ?? "").ToLowerInvariant() switch
    {
        "ara" => "AR",
        "ben" => "BN",
        "cze" or "ces" => "CS",
        "deu" or "ger" => "DE",
        "eng" => "EN-US",
        // 게임은 ESP=Español (Latinoamérica), SPA=Español (Castellano) 로 분리한다.
        "esp" => "ES-419",
        "spa" => "ES",
        "fil" or "tgl" => "TL",
        "fin" => "FI",
        "fra" or "fre" => "FR",
        "gre" or "ell" => "EL",
        "hin" => "HI",
        "ind" => "ID",
        "ita" => "IT",
        "jpn" => "JA",
        "kor" => "KO",
        // 게임의 MAL 은 Bahasa Melayu(말레이어) — ISO 639-2 의 Malayalam 이 아니다.
        "mal" or "msa" or "may" => "MS",
        "nld" or "dut" => "NL",
        "nor" or "nob" => "NB",
        "pol" => "PL",
        "por" => "PT-PT",
        "ptb" => "PT-BR",
        "rus" => "RU",
        "swe" => "SV",
        "tha" => "TH",
        "tur" => "TR",
        "ukr" => "UK",
        "vie" => "VI",
        "zhs" or "chs" or "zh-hans" => "ZH-HANS",
        "zht" or "cht" or "zh-hant" => "ZH-HANT",
        _ => null,
    };

    /// <summary>STS 원문 언어 → DeepL source_lang(지역 변형 없는 베이스). 모르면 null(자동 감지).</summary>
    private static string? SourceCode(string stsLang) => (stsLang ?? "").ToLowerInvariant() switch
    {
        "ara" => "AR",
        "ben" => "BN",
        "cze" or "ces" => "CS",
        "deu" or "ger" => "DE",
        "eng" => "EN",
        "esp" or "spa" => "ES",
        "fil" or "tgl" => "TL",
        "fin" => "FI",
        "fra" or "fre" => "FR",
        "gre" or "ell" => "EL",
        "hin" => "HI",
        "ind" => "ID",
        "ita" => "IT",
        "jpn" => "JA",
        "kor" => "KO",
        "mal" or "msa" or "may" => "MS",
        "nld" or "dut" => "NL",
        "nor" or "nob" => "NB",
        "pol" => "PL",
        "ptb" or "por" => "PT",
        "rus" => "RU",
        "swe" => "SV",
        "tha" => "TH",
        "tur" => "TR",
        "ukr" => "UK",
        "vie" => "VI",
        "zhs" or "zht" or "chs" or "cht" => "ZH",
        _ => null,
    };

    /// <summary>
    /// STS 언어 코드 → 프롬프트에 쓸 영문 언어명. OpenAI 호환 공급자는 벤더 코드가 아니라
    /// 사람이 읽는 이름을 쓰므로 게임 언어 30종을 전부 덮는다. 모르면 코드 그대로.
    /// </summary>
    public static string LangName(string stsLang) => (stsLang ?? "").ToLowerInvariant() switch
    {
        "ara" => "Arabic",
        "ben" => "Bengali",
        "cze" or "ces" => "Czech",
        "deu" or "ger" => "German",
        "eng" => "English",
        "esp" => "Latin American Spanish",
        "spa" => "Castilian Spanish",
        "fil" or "tgl" => "Filipino",
        "fin" => "Finnish",
        "fra" or "fre" => "French",
        "gre" or "ell" => "Greek",
        "hin" => "Hindi",
        "ind" => "Indonesian",
        "ita" => "Italian",
        "jpn" => "Japanese",
        "kor" => "Korean",
        "mal" or "msa" or "may" => "Malay",
        "nld" or "dut" => "Dutch",
        "nor" or "nob" => "Norwegian Bokmal",
        "pol" => "Polish",
        "por" => "European Portuguese",
        "ptb" => "Brazilian Portuguese",
        "rus" => "Russian",
        "swe" => "Swedish",
        "tha" => "Thai",
        "tur" => "Turkish",
        "ukr" => "Ukrainian",
        "vie" => "Vietnamese",
        "zhs" or "chs" or "zh-hans" => "Simplified Chinese",
        "zht" or "cht" or "zh-hant" => "Traditional Chinese",
        _ => stsLang ?? "",
    };

    /// <summary>
    /// 이 (STS) 언어를 현재 공급자로 번역할 수 있는지. DeepL 은 벤더 코드 매핑이 있어야 하고,
    /// OpenAI 호환 공급자는 언어명만 있으면 되므로 사실상 전부 가능하다.
    /// </summary>
    public static bool SupportsLanguage(string stsLang, AutoConfig cfg) =>
        cfg.Provider == AutoProvider.DeepL
            ? DeepLTarget(stsLang) != null
            : !string.IsNullOrWhiteSpace(stsLang);

    /// <summary>
    /// 설정과 대상 언어를 검사해 공급자별 target 문자열(DeepL 코드 / 영문 언어명)을 낸다.
    /// 쓸 수 없으면 null + 사용자에게 보일 이유를 <paramref name="error"/> 에 담는다.
    /// </summary>
    private static string? ResolveTarget(string lang, AutoConfig cfg, out string error)
    {
        error = "";
        if (cfg.Provider == AutoProvider.OpenAiCompatible)
        {
            if (string.IsNullOrWhiteSpace(cfg.BaseUrl) || string.IsNullOrWhiteSpace(cfg.Model))
            {
                error = "The AI endpoint isn't set up — fill in its address and model first.";
                return null;
            }
            return LangName(lang);
        }

        if (string.IsNullOrWhiteSpace(cfg.DeepLKey)) { error = "DeepL API key is not set."; return null; }
        string? t = DeepLTarget(lang);
        if (t == null) error = $"This mod has no DeepL mapping for language '{lang}' yet.";
        return t;
    }

    /// <summary>
    /// 편집기 JSON(<paramref name="editorJson"/>)의 빈 값을 DeepL 로 채운 새 JSON 을 만든다.
    /// 원문은 <paramref name="mod"/> 의 eng 테이블에서 가져온다. 자동 저장하지 않는다.
    /// 반환: (성공, 새 JSON 문자열, 채운 키 수, 검증 탈락 수, 오류메시지).
    /// </summary>
    public static async Task<(bool ok, string json, int count, int skipped, string error)> FillEditorAsync(
        SupportedMod mod, string table, string lang, string editorJson, AutoConfig cfg)
    {
        string? target = ResolveTarget(lang, cfg, out string cfgErr);
        if (target == null) return (false, editorJson, 0, 0, cfgErr);

        Dictionary<string, string>? cur;
        try { cur = JsonSerializer.Deserialize<Dictionary<string, string>>(editorJson, LocJson.Read); }
        catch (Exception ex) { return (false, editorJson, 0, 0, "Current JSON is invalid — fix it first: " + ex.Message); }
        if (cur == null) return (false, editorJson, 0, 0, "Current JSON top-level is not an object.");

        var eng = mod.EngByTable.TryGetValue(table, out var e) ? e : new Dictionary<string, string>();
        // 폴더 이름(SourceLang)이 아닌 실제 텍스트 언어(ContentLang)로 source_lang 을 잡는다 —
        // 한국어를 eng/ 에 넣은 모드에 EN 을 보내면 오역되므로.
        // 단, 원문이 혼합(부분 번역)이면 항목마다 언어가 달라 단일 source_lang 이 오히려 해가 된다
        // → source 를 비워 DeepL 자동감지에 맡긴다(영어 항목은 그대로, 섞인 중국어 항목만 번역).
        string? source = mod.HasMixedSource ? null : SourceCode(mod.ContentLang);

        try
        {
            var (n, skipped, err) = await FillDictAsync(eng, cur, source, target, lang, cfg);
            if (err.Length > 0) return (false, editorJson, 0, 0, err);
            if (n == 0 && skipped == 0)
                return (false, editorJson, 0, 0,
                    "Nothing to fill — every key is already translated (or has no source text).");
            if (n == 0)
                return (false, editorJson, 0, skipped,
                    $"All {skipped} DeepL result(s) failed the {{format}} safety check — translate them by hand.");
            return (true, TranslationStore.ToPrettyJson(cur), n, skipped, "");
        }
        catch (Exception ex)
        {
            return (false, editorJson, 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// 한 모드/언어의 *모든* 테이블에서 빈 항목을 DeepL 로 채워 각 override 파일에 저장한다.
    /// (편집기 검수 단계가 없으므로 디스크에 바로 쓴다 — 이후 편집기에서 개별 수정 가능.)
    /// <paramref name="progress"/>(현재순번, 총개수, 테이블명)로 진행 알림. API 오류(쿼터 등) 시 중단.
    /// 반환: (성공, 채운 항목 수, 검증 탈락 수, 채운 파일 수, 경고/오류 — 부분성공이면 마지막 오류를 경고로).
    /// </summary>
    public static async Task<(bool ok, int filled, int skipped, int files, string error)> FillAllTablesAsync(
        SupportedMod mod, string lang, AutoConfig cfg, Action<int, int, string>? progress)
    {
        string? target = ResolveTarget(lang, cfg, out string cfgErr);
        if (target == null) return (false, 0, 0, 0, cfgErr);
        // 실제 텍스트 언어(ContentLang)로 source_lang 을 잡는다(폴더 이름 아님).
        // 혼합 원문은 항목별 언어가 달라 자동감지에 맡긴다(위 FillEditorAsync 와 동일 이유).
        string? source = mod.HasMixedSource ? null : SourceCode(mod.ContentLang);

        var tables = mod.EngByTable.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
        int total = 0, totalSkipped = 0, files = 0, idx = 0;
        string lastErr = "";
        foreach (var table in tables)
        {
            progress?.Invoke(++idx, tables.Count, table);
            var eng = mod.EngByTable[table];
            Dictionary<string, string>? cur;
            try { cur = JsonSerializer.Deserialize<Dictionary<string, string>>(
                          TranslationStore.OverrideText(mod.Id, lang, table), LocJson.Read); }
            catch { continue; } // 깨진 override 파일은 건너뛴다(편집기에서 고쳐야 함)
            if (cur == null) continue;

            int n;
            try
            {
                var (c, skipped, err) = await FillDictAsync(eng, cur, source, target, lang, cfg);
                if (err.Length > 0) { lastErr = err; break; } // API 오류 → 나머지 중단
                n = c;
                totalSkipped += skipped;
            }
            catch (Exception ex) { lastErr = ex.Message; break; }

            if (n > 0)
            {
                TranslationStore.SaveOverrideText(mod.Id, lang, table, TranslationStore.ToPrettyJson(cur), eng);
                total += n; files++;
            }
        }

        if (lastErr.Length > 0 && total == 0) return (false, 0, totalSkipped, 0, lastErr);
        return (true, total, totalSkipped, files, lastErr); // 부분 성공 시 lastErr 은 경고로 전달
    }

    // ── 번역 결과 안전성 검증 ───────────────────────────────────

    private static readonly Regex PhContentRx =
        new(@"<ph>(.*?)</ph>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// 한 문자열의 '보존 대상 토큰' 시그니처(순서 무관 멀티셋). 번역 파이프라인과 동일한
    /// <see cref="Mask"/> 토크나이저를 그대로 재사용하므로 규칙 드리프트가 없다:
    ///   · ph 토큰 = 중괄호 변수 <c>{..}</c> · <c>!Var!</c> 실행 변수 · <c>[img]/[sprite]/[icon]</c>
    ///     데이터 태그 · 단독 <c>[..]</c>/<c>&lt;..&gt;</c> 토큰 · 선두 BaseLib <c>#</c> 마커.
    ///   · tag = 짝 태그(<c>[gold]</c>/<c>[color=x]</c>/<c>&lt;b&gt;</c> 등)의 여는 마커.
    /// 반환 두 멀티셋을 원문·결과에서 비교하면 토큰 유실/발명/중복을 한 번에 검출한다.
    /// </summary>
    private static (Dictionary<string, int> phs, Dictionary<string, int> tags) TokenSignature(string s)
    {
        var masked = Mask(s);
        var phs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match mt in PhContentRx.Matches(masked.Xml))
            Bump(phs, mt.Groups[1].Value);
        var tags = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in masked.Pairs) Bump(tags, p.open);
        return (phs, tags);
    }

    private static void Bump(Dictionary<string, int> m, string k) => m[k] = m.GetValueOrDefault(k) + 1;

    private static bool SameMultiset(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, c) in a)
            if (b.GetValueOrDefault(k) != c) return false;
        return true;
    }

    /// <summary>
    /// DeepL 결과가 게임에 넣어도 안전한지 검증. 두 축:
    ///   · 문법: SimpleLoc 변환 후 결과가 SmartFormat 파싱을 통과해야 함(주입 게이트와 동일 기준 —
    ///     깨진 항목은 렌더링마다 예외/크래시 → 렉).
    ///   · 토큰 보존: 원문의 모든 보존 대상 토큰(중괄호 변수·<c>!Var!</c>·데이터 태그·하이라이트
    ///     짝태그·<c>#</c> 마커)이 개수까지 그대로 있어야 함. 유실=수치/아이콘/색 빠진 설명,
    ///     발명=런타임 미지 변수 예외(문법은 유효해 LocValidator 로는 안 잡힘). 순서 무관이라
    ///     DeepL 의 정상 어순 재배열은 통과한다.
    /// 원문 자체가 깨진 값이면 결과에 같은 기준을 강요하지 않는다(주입 게이트가 어차피 거른다).
    /// </summary>
    internal static bool IsSafeResult(string source, string result)
    {
        if (!TranslationSync.TryValidateFormat(SimpleLocCompat.Apply(source), out _))
            return true; // 원문부터 깨짐 — 비교 무의미
        if (!TranslationSync.TryValidateFormat(SimpleLocCompat.Apply(result), out _))
            return false;

        var (srcPh, srcTag) = TokenSignature(source);
        var (resPh, resTag) = TokenSignature(result);
        return SameMultiset(srcPh, resPh) && SameMultiset(srcTag, resTag);
    }

    /// <summary>
    /// <paramref name="cur"/> 의 빈 값을, <paramref name="eng"/> 원문을 DeepL 번역해 채운다(in-place).
    /// 안전성 검증(IsSafeResult)에 실패한 결과는 채우지 않고 빈 값으로 남긴다(수동 번역 대상 —
    /// 편집기의 "Next empty ▼" 로 찾을 수 있다).
    /// 반환: (채운 키 수, 검증 탈락 수, 오류메시지). 채울 게 없으면 (0, 0, "").
    /// </summary>
    private static async Task<(int count, int skipped, string error)> FillDictAsync(
        Dictionary<string, string> eng, Dictionary<string, string> cur,
        string? source, string target, string stsTarget, AutoConfig cfg)
    {
        // 대상 = 값이 비어 있고, 원문에 번역할 텍스트가 있는 키. 기존 키 순서를 유지.
        var keys = cur.Where(kv => string.IsNullOrEmpty(kv.Value)
                                   && eng.TryGetValue(kv.Key, out var sv)
                                   && !string.IsNullOrWhiteSpace(sv))
                      .Select(kv => kv.Key)
                      .ToList();
        if (keys.Count == 0) return (0, 0, "");

        var masked = keys.Select(k => Mask(eng[k])).ToArray();
        var translated = new string[masked.Length];

        int i = 0;
        while (i < masked.Length)
        {
            int n = 0, bytes = 0;
            var batch = new List<string>();
            while (i + n < masked.Length && n < MaxBatchCount
                   && (n == 0 || bytes + masked[i + n].Xml.Length <= MaxBatchChars))
            {
                batch.Add(masked[i + n].Xml);
                bytes += masked[i + n].Xml.Length;
                n++;
            }
            var outs = await TranslateBatch(batch, source, target, cfg);
            if (outs.Count != batch.Count)
                return (0, 0, $"{cfg.ProviderName} returned {outs.Count} results for {batch.Count} inputs.");
            for (int j = 0; j < n; j++) translated[i + j] = outs[j];
            i += n;
        }

        int filled = 0, skipped = 0;
        for (int k = 0; k < keys.Count; k++)
        {
            // 빈 결과 = 공급자가 그 항목을 빠뜨렸다는 뜻(LLM 은 항목을 합치거나 흘린다).
            // 원문에 보존할 토큰이 없으면 빈 값도 IsSafeResult 를 통과해 "번역됨(빈칸)" 으로
            // 잘못 집계되므로, 검증 전에 먼저 걸러 낸다.
            if (string.IsNullOrWhiteSpace(translated[k]))
            {
                skipped++;
                MainFile.Logger.Warn(
                    $"[Sts2ModTranslator] {cfg.ProviderName} 가 항목을 비워 반환 — 빈칸 유지: {keys[k]}");
                continue;
            }

            string result = Unmask(translated[k], masked[k], stsTarget);
            if (!IsSafeResult(eng[keys[k]], result))
            {
                skipped++;
                MainFile.Logger.Warn(
                    $"[Sts2ModTranslator] {cfg.ProviderName} result failed the format safety check — left empty: {keys[k]}");
                continue;
            }
            cur[keys[k]] = result;
            filled++;
        }
        return (filled, skipped, "");
    }

    /// <summary>
    /// 선택된 공급자로 한 배치를 번역한다. 반환 목록은 <b>입력과 같은 길이·같은 순서</b>여야 하며,
    /// 공급자가 빠뜨린 항목은 빈 문자열로 채워진다(호출부가 그 항목만 빈칸으로 남긴다).
    /// </summary>
    private static Task<List<string>> TranslateBatch(
        List<string> xmls, string? source, string target, AutoConfig cfg) =>
        cfg.Provider == AutoProvider.OpenAiCompatible
            ? TranslateBatchOpenAi(xmls, target, cfg)
            : TranslateBatchDeepL(xmls, source, target, cfg.DeepLKey);

    // ── DeepL 호출 ──────────────────────────────────────────────
    private static async Task<List<string>> TranslateBatchDeepL(
        List<string> xmls, string? source, string target, string key)
    {
        string trimmed = key.Trim();
        // 무료 키는 ':fx' 로 끝난다 → api-free 호스트.
        string host = trimmed.EndsWith(":fx", StringComparison.Ordinal)
            ? "https://api-free.deepl.com"
            : "https://api.deepl.com";

        var form = new List<KeyValuePair<string, string>>
        {
            new("target_lang", target),
            new("tag_handling", "xml"),
            new("ignore_tags", "ph"),
            new("preserve_formatting", "1"),
            new("outline_detection", "0"),
        };
        if (source != null) form.Add(new("source_lang", source));
        foreach (var t in xmls) form.Add(new("text", t));

        using var req = new HttpRequestMessage(HttpMethod.Post, host + "/v2/translate")
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + trimmed);

        // 도달 실패(DNS·연결 거부·타임아웃)는 .NET 기본 메시지가 "An error occurred while sending
        // the request." 뿐이라 단서가 없다 → 지역 차단 가능성을 명시해 오진을 막는다.
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req); }
        catch (TaskCanceledException)
        {
            throw new Exception($"{UnreachableTag} — the request timed out. {RegionNote}");
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"{UnreachableTag} — {ex.Message} {RegionNote}");
        }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception(DescribeError((int)resp.StatusCode, body));
            return ParseTranslations(body);
        }
    }

    /// <summary>DeepL 응답 JSON 에서 translations[].text 를 순서대로 뽑는다.</summary>
    private static List<string> ParseTranslations(string body)
    {
        var result = new List<string>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("translations", out var arr) &&
            arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                result.Add(item.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "");
        return result;
    }

    private static string DescribeError(int status, string body)
    {
        string hint = status switch
        {
            400 => "DeepL rejected the request — this target language may be unavailable for your key.",
            401 => "Authentication failed — check your DeepL API key. "
                   + "Copy it whole, including the ':fx' suffix that free keys end with.",
            // 403 은 잘못된 키와 지역 차단이 겹치는 코드다 — 예전엔 401 과 묶어 "키를 확인하라"고만
            // 안내해 차단 지역 사용자를 엉뚱한 곳으로 보냈다.
            403 => $"{RefusedTag} — either the key is wrong, or DeepL doesn't serve your region. {RegionNote}",
            429 => "Too many requests — wait a moment and retry.",
            456 => "DeepL quota exceeded for this key (monthly character limit).",
            _ => "DeepL request failed.",
        };
        string snippet = body.Length > 200 ? body.Substring(0, 200) : body;
        return $"{hint} (HTTP {status}) {snippet}".Trim();
    }

    // ── OpenAI 호환 엔드포인트 호출 ─────────────────────────────
    //
    // ★왜 이 형식인가: OpenAI 의 /chat/completions 요청 모양이 사실상 표준이라, 사용자가
    //   주소·모델·키만 넣으면 상용(OpenAI·OpenRouter…)부터 로컬(Ollama·LM Studio)까지
    //   같은 코드로 붙는다. DeepL 이 서비스하지 않는 지역의 탈출구가 된다.
    //
    // ★순서 보장: DeepL 은 translations[] 를 요청 순서대로 1:1 로 준다. LLM 은 그렇지 않다 —
    //   항목을 합치거나 흘린다. 그래서 입출력을 모두 <b>번호를 키로 하는 JSON 객체</b>로
    //   주고받고, 빠진 번호는 빈 문자열로 채워 그 항목만 빈칸으로 남긴다(배치 전체 실패 아님).
    //
    // ★response_format 을 보내지 않는 이유: 호환 서버 중 이 필드를 모르면 400 으로 거절하는
    //   구현이 있다. 호환성이 이 기능의 존재 이유이므로 프롬프트로 지시하고 응답은 관대하게 판다.

    private static string SystemPrompt(string langName) =>
        $"You translate text from a video game into {langName}.\n"
        + "You are given a JSON object whose keys are item numbers. Reply with ONLY a JSON object "
        + "using the same keys, where each value is that item's translation.\n"
        + "Rules:\n"
        + "- Translate every item. If you truly cannot translate one, omit its key entirely.\n"
        + "- Keep every <ph>...</ph> and <gN>...</gN> tag exactly as it appears, with the same "
        + "numbers and the same count. Never translate, reorder, renumber, add or drop them.\n"
        + "- Text inside <ph>...</ph> is a placeholder token. Copy it verbatim.\n"
        + "- Translate only the human-readable words. Add no notes, no explanations, no markdown.";

    /// <summary>
    /// 채팅 요청 본문 직렬화 옵션. ★HTML-safe 기본 이스케이프를 끈다 — 켜 두면 마스킹 태그가
    /// <c>&lt;ph&gt;</c> 가 아니라 <c>\u003Cph\u003E</c> 로 실려 나가 모델이 <b>실제 태그를 보지 못한다</b>.
    /// 모델이 본 대로 <c>\u003C…</c> 를 그대로 돌려주면 Unmask 가 태그를 못 찾아 안전검사에서 전부
    /// 탈락하고, 결과적으로 모든 항목이 빈칸이 된다. (본문은 application/json 이라 HTML 이스케이프가
    /// 필요 없다 — 루프백 end-to-end 테스트로 잡은 결함.)
    /// </summary>
    private static readonly JsonSerializerOptions ChatJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task<List<string>> TranslateBatchOpenAi(
        List<string> xmls, string langName, AutoConfig cfg)
    {
        string url = AutoConfig.ChatUrl(cfg.BaseUrl);
        if (url.Length == 0) throw new Exception("The AI endpoint address is empty.");

        // 입력을 번호 키 JSON 으로. (구분자 기반으로 넘기면 본문에 줄바꿈이 있을 때 깨진다.)
        var inObj = new Dictionary<string, string>();
        for (int i = 0; i < xmls.Count; i++) inObj[(i + 1).ToString()] = xmls[i];

        var payload = new Dictionary<string, object>
        {
            ["model"] = cfg.Model.Trim(),
            ["temperature"] = 0,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = SystemPrompt(langName) },
                new Dictionary<string, string> { ["role"] = "user",
                    ["content"] = JsonSerializer.Serialize(inObj, ChatJson) },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ChatJson), Encoding.UTF8, "application/json"),
        };
        string key = (cfg.ApiKey ?? "").Trim();
        if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req); }
        catch (TaskCanceledException)
        {
            throw new Exception($"Couldn't reach the AI endpoint — the request timed out. ({url})");
        }
        catch (HttpRequestException ex)
        {
            throw new Exception($"Couldn't reach the AI endpoint — {ex.Message} ({url})");
        }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                string snippet = body.Length > 300 ? body.Substring(0, 300) : body;
                throw new Exception(
                    $"The AI endpoint rejected the request (HTTP {(int)resp.StatusCode}). "
                    + $"Check the address, the model name and the key. {snippet}".Trim());
            }
            return ParseChatTranslations(body, xmls.Count);
        }
    }

    /// <summary>
    /// chat/completions 응답에서 번호 키 JSON 을 뽑아 입력 개수만큼의 목록으로 만든다.
    /// 빠진 번호·형식 붕괴는 <b>예외가 아니라 빈 문자열</b>로 돌려준다 — 한 항목의 실패가
    /// 배치 전체를 죽이지 않게(호출부가 그 항목만 빈칸으로 남긴다).
    /// </summary>
    internal static List<string> ParseChatTranslations(string body, int expected)
    {
        // ★null 이 아니라 빈 문자열로 채운다 — 반환 계약이 "빠진 항목 = 빈 문자열" 이고,
        // null 이 섞이면 호출부/테스트가 계약과 어긋난다.
        var outs = Enumerable.Repeat(string.Empty, expected).ToList();
        string content;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("choices", out var ch)
                || ch.ValueKind != JsonValueKind.Array || ch.GetArrayLength() == 0)
                throw new Exception("the response had no 'choices'");
            content = ch[0].TryGetProperty("message", out var msg)
                      && msg.TryGetProperty("content", out var c)
                ? c.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            throw new Exception($"The AI endpoint returned something unexpected — {ex.Message}.");
        }

        // 모델이 ```json 펜스나 잡담을 섞는 경우가 흔하다 → 첫 '{' 부터 마지막 '}' 까지만 판다.
        int a = content.IndexOf('{'), b = content.LastIndexOf('}');
        if (a < 0 || b <= a)
        {
            MainFile.Logger.Warn("[Sts2ModTranslator] AI 응답에 JSON 객체가 없음 — 배치 전체를 빈칸 처리.");
            return outs;
        }

        try
        {
            using var doc = JsonDocument.Parse(content.Substring(a, b - a + 1));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return outs;
            for (int i = 0; i < expected; i++)
                if (doc.RootElement.TryGetProperty((i + 1).ToString(), out var v)
                    && v.ValueKind == JsonValueKind.String)
                    outs[i] = v.GetString() ?? "";
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] AI 응답 JSON 파싱 실패 — 빈칸 처리: {ex.Message}");
        }
        return outs;
    }

    /// <summary>
    /// 설정이 실제로 동작하는지 짧은 문장 하나로 왕복 확인한다. 주소·모델명 오타는 BYO 엔드포인트의
    /// 1순위 실패 모드인데, 번역을 통째로 돌려 본 뒤에야 알게 되면 진단이 어렵다.
    /// 반환: (성공, 사용자에게 보일 메시지).
    /// </summary>
    public static async Task<(bool ok, string message)> TestAsync(string lang, AutoConfig cfg)
    {
        string? target = ResolveTarget(lang, cfg, out string err);
        if (target == null) return (false, err);

        const string probe = "Deal <ph>{0}</ph> damage.";
        try
        {
            var outs = await TranslateBatch(new List<string> { probe }, "EN", target, cfg);
            if (outs.Count != 1 || string.IsNullOrWhiteSpace(outs[0]))
                return (false, $"{cfg.ProviderName} connected but returned nothing. "
                               + "If this is an AI endpoint, try a different model.");
            return (true, $"OK — \"{probe}\" → \"{outs[0].Trim()}\"");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ── 토큰 마스킹 ─────────────────────────────────────────────

    /// <summary>한 문자열의 마스킹 결과 = DeepL 로 보낼 XML + gN(짝 태그) 복원 맵.</summary>
    private sealed class Masked
    {
        public string Xml = "";
        // gN 복원용 (열기/닫기 원문 마크업 + 감싼 원문 텍스트). 인덱스 N = 태그 번호.
        // inner 는 글로서리 키워드 강제(EnforceGlossary)에 쓴다.
        public readonly List<(string open, string close, string inner)> Pairs = new();
    }

    private static Masked Mask(string s)
    {
        var m = new Masked();
        var sb = new StringBuilder(s.Length + 16);
        // BaseLib SimpleLoc opt-in 마커(맨 앞 '#')는 통째 보존. DeepL 이 문장 앞 기호를 떨어뜨리면
        // 주입 시 SimpleLoc 변환이 안 걸려 '!Var!' 원형이 게임에 노출된다. (최상위에서만 마커.)
        if (s.StartsWith('#')) { sb.Append("<ph>#</ph>"); s = s.Substring(1); }
        MaskInto(s, sb, m);
        m.Xml = sb.ToString();
        return m;
    }

    // 짝 태그 우선, 그다음 단독 토큰, 나머지는 이스케이프한 일반 텍스트로 XML 을 조립.
    private static void MaskInto(string s, StringBuilder sb, Masked m)
    {
        int i = 0;
        while (i < s.Length)
        {
            // 중괄호 변수 {..}(중첩 {InCombat:..{CombatHeal}..} 포함)는 균형 스캔으로 통째 보존.
            if (s[i] == '{')
            {
                int depth = 0, j = i;
                for (; j < s.Length; j++)
                {
                    if (s[j] == '{') depth++;
                    else if (s[j] == '}' && --depth == 0) { j++; break; }
                }
                sb.Append("<ph>").Append(XmlEscape(s.Substring(i, j - i))).Append("</ph>");
                i = j; continue;
            }
            // 데이터 짝 태그([img]path[/img])는 안쪽을 번역시키지 않고 전체를 통째로 보존.
            Match mo = OpaquePairedRx.Match(s, i);
            if (mo.Success && mo.Index == i)
            {
                sb.Append("<ph>").Append(XmlEscape(mo.Value)).Append("</ph>");
                i += mo.Length; continue;
            }
            Match mb = PairedBbRx.Match(s, i);
            if (mb.Success && mb.Index == i)
            {
                EmitPair($"[{mb.Groups[1].Value}{mb.Groups[2].Value}]", $"[/{mb.Groups[1].Value}]",
                    mb.Groups[3].Value, sb, m);
                i += mb.Length; continue;
            }
            Match ma = PairedAngleRx.Match(s, i);
            if (ma.Success && ma.Index == i)
            {
                EmitPair($"<{ma.Groups[1].Value}{ma.Groups[2].Value}>", $"</{ma.Groups[1].Value}>",
                    ma.Groups[3].Value, sb, m);
                i += ma.Length; continue;
            }
            Match mv = VarBangRx.Match(s, i);
            if (mv.Success && mv.Index == i)
            {
                sb.Append("<ph>").Append(XmlEscape(mv.Value)).Append("</ph>");
                i += mv.Length; continue;
            }
            Match ms = StandaloneRx.Match(s, i);
            if (ms.Success && ms.Index == i)
            {
                sb.Append("<ph>").Append(XmlEscape(ms.Value)).Append("</ph>");
                i += ms.Length; continue;
            }
            sb.Append(XmlEscape(s[i].ToString()));
            i++;
        }
    }

    // 짝 태그를 <gN>…</gN> 로 바꾸고 안쪽은 재귀 처리(중첩 태그·번역 대상 텍스트).
    private static void EmitPair(string open, string close, string inner, StringBuilder sb, Masked m)
    {
        int n = m.Pairs.Count;
        m.Pairs.Add((open, close, inner));
        sb.Append("<g").Append(n).Append('>');
        MaskInto(inner, sb, m);
        sb.Append("</g").Append(n).Append('>');
    }

    // DeepL 이 무번역 플레이스홀더(<ph>…</ph>)를 외래어로 보고 양옆에 덧붙이는 따옴표 쌍을 제거.
    // 원문엔 없던 장식이다. 여는/닫는 따옴표가 토큰에 딱 붙은 '쌍'일 때만(한쪽만이면 애매 → 보존).
    private static readonly Regex PhQuoteRx = new(
        "[‘’'\"“”「」『』«»„‚‹›](<ph>.*?</ph>)[‘’'\"“”「」『』«»„‚‹›]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // 하이라이트 태그([gold]/[color])는 키워드만 감싸야 하는데, DeepL 이 번역하며 한국어 조사(을/이/의)나
    // 따옴표를 태그 '안'에 넣는다("[gold]힘이[/gold]", "[gold]‘감염[/gold]’"). 조사는 태그 밖으로 옮기고
    // 경계 따옴표는 제거한다. best-effort(형태소 분석이 아니라 조사 목록 매칭) — 드물게 과/미교정 가능.
    private static readonly char[] Quotes = "‘’'\"“”「」『』«»„‚‹›".ToCharArray();
    // 안전 뒤조사: 음역어 끝에 드문 음절 → 남는 글자가 한글이면 무조건 분리.
    private static readonly string[] SafeTrailJosa =
        { "으로써", "으로서", "에게서", "이라는", "이라고", "으로", "에서", "에게", "을", "를", "의", "에" };
    // 위험 뒤조사(이/가/은/는): 음역어 끝에 흔함("리플레이"=Replay) → 남는 base 가 '알려진 키워드'일 때만 분리.
    private static readonly string[] GuardedTrailJosa = { "이", "가", "은", "는" };
    // 앞 조사(뒤에 공백이 붙은 경우만 — "의 생명력" 처럼 명백한 오배치).
    private static readonly string[] LeadJosa = { "의", "을", "를", "에" };

    // 하이라이트로 감싸이는 STS 키워드의 한국어 번역(base). GuardedTrailJosa 분리 판단에만 쓴다
    // ("힘이"→힘+이 분리 O / "리플레이"→리플레 미등록이라 분리 X). 향후 글로서리 용어표와 통합 가능.
    private static readonly System.Collections.Generic.HashSet<string> KnownTermBases = new()
    {
        "힘", "위력", "취약", "약화", "민첩성", "생명력", "감염", "블록", "방어도", "방어",
        "독", "재생", "회복", "출혈", "화상", "표식", "실크", "정수", "에너지", "도구",
        "파편", "샤드", "저주", "소진", "유물", "포션", "골드",
    };

    private static bool IsHighlight(string open) =>
        open.Contains("gold", StringComparison.OrdinalIgnoreCase)
        || open.StartsWith("[color", StringComparison.OrdinalIgnoreCase)
        || open.StartsWith("<color", StringComparison.OrdinalIgnoreCase);

    private static bool IsHangul(char c) => c >= 0xAC00 && c <= 0xD7A3;
    private static bool IsQuote(char c) => Array.IndexOf(Quotes, c) >= 0;

    // 하이라이트 <gN> 스팬 정리. gN 복원 '전에' 실행.
    //  · 경계 따옴표 제거 = 언어 무관(외래어 인용 아티팩트는 모든 언어에서 발생).
    //  · 조사/입자 이동 = 교착어(한국어 등)만. target 언어별 핸들러로 분리 — 유럽어 등은 no-op.
    private static string TrimHighlightSpans(string xml, Masked m, string target)
    {
        Func<string, (string prefix, string suffix, string content)>? particles = ParticleMover(target);
        for (int n = 0; n < m.Pairs.Count; n++)
        {
            if (!IsHighlight(m.Pairs[n].open)) continue;
            // 태그 바로 앞/뒤의 따옴표(그룹1·3)도 함께 잡아 제거(밖에 덧붙은 경우).
            var rx = new Regex($"[‘’'\"“”「」『』«»„‚‹›]?<g{n}>(.*?)</g{n}>[‘’'\"“”「」『』«»„‚‹›]?",
                               RegexOptions.Singleline);
            xml = rx.Replace(xml, mt =>
            {
                string c = mt.Groups[1].Value;
                while (c.Length > 0 && IsQuote(c[0])) c = c.Substring(1);
                while (c.Length > 0 && IsQuote(c[^1])) c = c.Substring(0, c.Length - 1);
                string prefix = "", suffix = "";
                if (particles != null) (prefix, suffix, c) = particles(c);
                return prefix + $"<g{n}>" + c + $"</g{n}>" + suffix;
            });
        }
        return xml;
    }

    /// <summary>
    /// 대상 언어의 '하이라이트 태그 안에 딸려온 문법 입자를 밖으로 빼는' 핸들러. 규칙이 없으면 null
    /// (= 따옴표 정리만, 내용 불변). 교착어만 등록 — JA 등은 여기 한 줄 추가로 확장.
    /// </summary>
    private static Func<string, (string, string, string)>? ParticleMover(string target) =>
        target.StartsWith("KO", StringComparison.OrdinalIgnoreCase) ? MoveKoreanJosa : null;

    // 한국어: 하이라이트 안에 딸려온 조사를 태그 밖으로. 반환 (앞으로뺄것, 뒤로뺄것, 남는내용).
    private static (string, string, string) MoveKoreanJosa(string c)
    {
        string prefix = "", suffix = "";
        foreach (var j in LeadJosa)
            if (c.StartsWith(j + " ", StringComparison.Ordinal))
            { prefix = j + " "; c = c.Substring(j.Length + 1); break; }
        foreach (var j in SafeTrailJosa)
            if (c.Length > j.Length && c.EndsWith(j, StringComparison.Ordinal)
                && IsHangul(c[c.Length - j.Length - 1]))
            { suffix = j; c = c.Substring(0, c.Length - j.Length); break; }
        if (suffix.Length == 0)
            foreach (var j in GuardedTrailJosa)
                if (c.Length > j.Length && c.EndsWith(j, StringComparison.Ordinal)
                    && KnownTermBases.Contains(c.Substring(0, c.Length - j.Length)))
                { suffix = j; c = c.Substring(0, c.Length - j.Length); break; }
        return (prefix, suffix, c);
    }

    // DeepL 이 인라인 태그를 재배열하며 <gN> 를 쪼개/중복시키는 경우(관측 0.1%대)를 1쌍으로 병합.
    // 원래 각 gN 은 정확히 1쌍이므로, 여러 개면 첫 <gN>~마지막 </gN> 사이의 내부 gN 태그를 제거.
    private static string CollapseDuplicateSpans(string xml, Masked m)
    {
        for (int n = 0; n < m.Pairs.Count; n++)
        {
            string o = $"<g{n}>", c = $"</g{n}>";
            if (CountOcc(xml, o) == 1 && CountOcc(xml, c) == 1) continue; // 정상(1쌍)
            int fo = xml.IndexOf(o, StringComparison.Ordinal);
            int lc = xml.LastIndexOf(c, StringComparison.Ordinal);
            if (fo < 0 || lc < 0 || lc < fo) continue;
            int innerStart = fo + o.Length;
            string inner = xml.Substring(innerStart, lc - innerStart).Replace(o, "").Replace(c, "");
            xml = xml.Substring(0, fo) + o + inner + c + xml.Substring(lc + c.Length);
        }
        return xml;
    }

    private static int CountOcc(string s, string sub)
    {
        int cnt = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { cnt++; i += sub.Length; }
        return cnt;
    }

    // STS 키워드의 게임 공식 번역(13개 언어). SlayTheSpire2.pck 의 powers/card_keywords/afflictions/orbs
    // /static_hover_tips 의 .title 에서 추출해 glossary.json(임베디드 리소스)로 동봉 — 언어당 수백 개.
    // 구조: { "kor": { "Vulnerable": "취약", ... }, "jpn": {...}, ... }. 언어 키는 STS 코드(대소문자 무관),
    // 영문 키워드는 카드 텍스트 표기와 정확히 일치(대소문자 구분). 없거나 로드 실패 시 빈 표(교정 no-op).
    private static readonly Dictionary<string, Dictionary<string, string>> Glossary = LoadGlossary();

    /// <summary>
    /// 한 언어의 공식 용어표(영문 키워드 → 정식 번역). 없으면 null.
    /// AiKitWriter 가 디스크로 덤프해 AI 에이전트가 같은 용어를 쓰게 한다 — glossary.json 은
    /// 임베디드 리소스라 폴더에서 작업하는 외부 도구는 원래 접근할 방법이 없다.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? GlossaryFor(string lang)
        => Glossary.TryGetValue(lang ?? "", out var g) && g.Count > 0 ? g : null;

    private static Dictionary<string, Dictionary<string, string>> LoadGlossary()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = typeof(AutoTranslator).Assembly;
            string? name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("glossary.json", StringComparison.OrdinalIgnoreCase));
            if (name == null) return result;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return result;
            using var r = new StreamReader(s, Encoding.UTF8);
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(r.ReadToEnd());
            if (raw != null)
                foreach (var (lang, map) in raw)
                    result[lang] = new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[Sts2ModTranslator] glossary 로드 실패(교정 비활성): {ex.Message}");
        }
        return result;
    }

    // <ph>/<gN> 래퍼를 벗기고(짝 태그는 원문 마크업으로 복원) XML 엔티티를 원복.
    private static string Unmask(string xml, Masked m, string stsTarget)
    {
        // josa/따옴표 정리는 DeepL 타깃 코드(KO 등)로 판단.
        string josaTarget = DeepLTarget(stsTarget) ?? (stsTarget ?? "").ToUpperInvariant();
        xml = PhQuoteRx.Replace(xml, "$1");           // 플레이스홀더 따옴표 제거(언어 무관)
        xml = CollapseDuplicateSpans(xml, m);         // DeepL 이 쪼갠 중복 <gN> 스팬 병합
        xml = EnforceGlossary(xml, m, stsTarget);     // 하이라이트 키워드 → 공식 용어 강제(DeepL 오역 교정)
        xml = TrimHighlightSpans(xml, m, josaTarget); // 하이라이트 정리(따옴표 공통 + 조사 KO만)
        // 뒤 번호부터 복원(<g1> 이 <g10> 안에 섞이지 않게 — '>' 로 이미 안전하지만 안전제일).
        for (int n = m.Pairs.Count - 1; n >= 0; n--)
            xml = xml.Replace($"<g{n}>", m.Pairs[n].open)
                     .Replace($"</g{n}>", m.Pairs[n].close);
        return XmlUnescape(xml.Replace("<ph>", "").Replace("</ph>", ""));
    }

    // <gN> 하이라이트로 감싼 원문이 정확히 글로서리 키워드면, 그 안의 (오)번역을 공식 용어로 교체.
    // 결정적 — DeepL 이 용어를 틀려도 여기서 바로잡는다. 용어표 없는 언어는 no-op.
    // (STS 카드 텍스트는 키워드를 [color]/[gold] 로 감싸므로 이 경로가 대부분을 커버.)
    private static string EnforceGlossary(string xml, Masked m, string? stsTarget)
    {
        if (!Glossary.TryGetValue(stsTarget ?? "", out var g) || g.Count == 0) return xml;
        for (int n = 0; n < m.Pairs.Count; n++)
        {
            string innerEng = (m.Pairs[n].inner ?? "").Trim();
            if (innerEng.Length == 0 || !g.TryGetValue(innerEng, out var term)) continue;
            var rx = new Regex($"<g{n}>.*?</g{n}>", RegexOptions.Singleline);
            xml = rx.Replace(xml, $"<g{n}>" + XmlEscape(term) + $"</g{n}>", 1);
        }
        return xml;
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string XmlUnescape(string s) => s
        .Replace("&lt;", "<").Replace("&gt;", ">")
        .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
        .Replace("&amp;", "&"); // &amp; 는 반드시 마지막
}
