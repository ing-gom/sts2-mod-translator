using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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

    // DeepL 요청당 보수적 상한(텍스트 개수 / 합산 바이트). 문서 한도(50개·128KiB)보다 낮게.
    private const int MaxBatchCount = 40;
    private const int MaxBatchChars = 90_000;

    /// <summary>STS 언어 코드 → DeepL target_lang. 지원 안 하면 null(호출부가 안내).</summary>
    public static string? DeepLTarget(string stsLang) => (stsLang ?? "").ToLowerInvariant() switch
    {
        "kor" => "KO",
        "jpn" => "JA",
        "zhs" or "chs" or "zh-hans" => "ZH-HANS",
        "zht" or "cht" or "zh-hant" => "ZH-HANT",
        "fra" or "fre" => "FR",
        "deu" or "ger" => "DE",
        "esp" or "spa" => "ES",
        "rus" => "RU",
        "ptb" => "PT-BR",
        "por" => "PT-PT",
        "ita" => "IT",
        "pol" => "PL",
        "nld" or "dut" => "NL",
        "tur" => "TR",
        "ukr" => "UK",
        "eng" => "EN-US",
        _ => null,
    };

    /// <summary>STS 원문 언어 → DeepL source_lang(지역 변형 없는 베이스). 모르면 null(자동 감지).</summary>
    private static string? SourceCode(string stsLang) => (stsLang ?? "").ToLowerInvariant() switch
    {
        "eng" => "EN",
        "kor" => "KO",
        "jpn" => "JA",
        "zhs" or "zht" or "chs" or "cht" => "ZH",
        "fra" or "fre" => "FR",
        "deu" or "ger" => "DE",
        "esp" or "spa" => "ES",
        "rus" => "RU",
        "ptb" or "por" => "PT",
        "ita" => "IT",
        "pol" => "PL",
        "nld" or "dut" => "NL",
        "tur" => "TR",
        "ukr" => "UK",
        _ => null,
    };

    /// <summary>이 (STS) 언어를 DeepL 로 번역할 수 있는지.</summary>
    public static bool SupportsLanguage(string stsLang) => DeepLTarget(stsLang) != null;

    /// <summary>
    /// 편집기 JSON(<paramref name="editorJson"/>)의 빈 값을 DeepL 로 채운 새 JSON 을 만든다.
    /// 원문은 <paramref name="mod"/> 의 eng 테이블에서 가져온다. 자동 저장하지 않는다.
    /// 반환: (성공, 새 JSON 문자열, 채운 키 수, 오류메시지).
    /// </summary>
    public static async Task<(bool ok, string json, int count, string error)> FillEditorAsync(
        SupportedMod mod, string table, string lang, string editorJson, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, editorJson, 0, "DeepL API key is not set.");

        string? target = DeepLTarget(lang);
        if (target == null)
            return (false, editorJson, 0, $"DeepL does not support language '{lang}'.");

        Dictionary<string, string>? cur;
        try { cur = JsonSerializer.Deserialize<Dictionary<string, string>>(editorJson); }
        catch (Exception ex) { return (false, editorJson, 0, "Current JSON is invalid — fix it first: " + ex.Message); }
        if (cur == null) return (false, editorJson, 0, "Current JSON top-level is not an object.");

        var eng = mod.EngByTable.TryGetValue(table, out var e) ? e : new Dictionary<string, string>();
        string? source = SourceCode(mod.SourceLang);

        try
        {
            var (n, err) = await FillDictAsync(eng, cur, source, target, apiKey);
            if (err.Length > 0) return (false, editorJson, 0, err);
            if (n == 0)
                return (false, editorJson, 0,
                    "Nothing to fill — every key is already translated (or has no source text).");
            return (true, TranslationStore.ToPrettyJson(cur), n, "");
        }
        catch (Exception ex)
        {
            return (false, editorJson, 0, ex.Message);
        }
    }

    /// <summary>
    /// 한 모드/언어의 *모든* 테이블에서 빈 항목을 DeepL 로 채워 각 override 파일에 저장한다.
    /// (편집기 검수 단계가 없으므로 디스크에 바로 쓴다 — 이후 편집기에서 개별 수정 가능.)
    /// <paramref name="progress"/>(현재순번, 총개수, 테이블명)로 진행 알림. API 오류(쿼터 등) 시 중단.
    /// 반환: (성공, 채운 항목 수, 채운 파일 수, 경고/오류 — 부분성공이면 마지막 오류를 경고로).
    /// </summary>
    public static async Task<(bool ok, int filled, int files, string error)> FillAllTablesAsync(
        SupportedMod mod, string lang, string apiKey, Action<int, int, string>? progress)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, 0, 0, "DeepL API key is not set.");
        string? target = DeepLTarget(lang);
        if (target == null) return (false, 0, 0, $"DeepL does not support language '{lang}'.");
        string? source = SourceCode(mod.SourceLang);

        var tables = mod.EngByTable.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
        int total = 0, files = 0, idx = 0;
        string lastErr = "";
        foreach (var table in tables)
        {
            progress?.Invoke(++idx, tables.Count, table);
            var eng = mod.EngByTable[table];
            Dictionary<string, string>? cur;
            try { cur = JsonSerializer.Deserialize<Dictionary<string, string>>(
                          TranslationStore.OverrideText(mod.Id, lang, table)); }
            catch { continue; } // 깨진 override 파일은 건너뛴다(편집기에서 고쳐야 함)
            if (cur == null) continue;

            int n;
            try
            {
                var (c, err) = await FillDictAsync(eng, cur, source, target, apiKey);
                if (err.Length > 0) { lastErr = err; break; } // API 오류 → 나머지 중단
                n = c;
            }
            catch (Exception ex) { lastErr = ex.Message; break; }

            if (n > 0)
            {
                TranslationStore.SaveOverrideText(mod.Id, lang, table, TranslationStore.ToPrettyJson(cur));
                total += n; files++;
            }
        }

        if (lastErr.Length > 0 && total == 0) return (false, 0, 0, lastErr);
        return (true, total, files, lastErr); // 부분 성공 시 lastErr 은 경고로 전달
    }

    /// <summary>
    /// <paramref name="cur"/> 의 빈 값을, <paramref name="eng"/> 원문을 DeepL 번역해 채운다(in-place).
    /// 반환: (채운 키 수, 오류메시지). 채울 게 없으면 (0, "").
    /// </summary>
    private static async Task<(int count, string error)> FillDictAsync(
        Dictionary<string, string> eng, Dictionary<string, string> cur,
        string? source, string target, string apiKey)
    {
        // 대상 = 값이 비어 있고, 원문에 번역할 텍스트가 있는 키. 기존 키 순서를 유지.
        var keys = cur.Where(kv => string.IsNullOrEmpty(kv.Value)
                                   && eng.TryGetValue(kv.Key, out var sv)
                                   && !string.IsNullOrWhiteSpace(sv))
                      .Select(kv => kv.Key)
                      .ToList();
        if (keys.Count == 0) return (0, "");

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
            var outs = await TranslateBatch(batch, source, target, apiKey);
            if (outs.Count != batch.Count)
                return (0, $"DeepL returned {outs.Count} results for {batch.Count} inputs.");
            for (int j = 0; j < n; j++) translated[i + j] = outs[j];
            i += n;
        }

        for (int k = 0; k < keys.Count; k++)
            cur[keys[k]] = Unmask(translated[k], masked[k], target);
        return (keys.Count, "");
    }

    // ── DeepL 호출 ──────────────────────────────────────────────
    private static async Task<List<string>> TranslateBatch(
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

        using var resp = await Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception(DescribeError((int)resp.StatusCode, body));

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
            401 or 403 => "Authentication failed — check your DeepL API key.",
            429 => "Too many requests — wait a moment and retry.",
            456 => "DeepL quota exceeded for this key (monthly character limit).",
            _ => "DeepL request failed.",
        };
        string snippet = body.Length > 200 ? body.Substring(0, 200) : body;
        return $"{hint} (HTTP {status}) {snippet}".Trim();
    }

    // ── 토큰 마스킹 ─────────────────────────────────────────────

    /// <summary>한 문자열의 마스킹 결과 = DeepL 로 보낼 XML + gN(짝 태그) 복원 맵.</summary>
    private sealed class Masked
    {
        public string Xml = "";
        // gN 복원용 (열기/닫기 원문 마크업). 인덱스 N = 태그 번호.
        public readonly List<(string open, string close)> Pairs = new();
    }

    private static Masked Mask(string s)
    {
        var m = new Masked();
        var sb = new StringBuilder(s.Length + 16);
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
        m.Pairs.Add((open, close));
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

    // <ph>/<gN> 래퍼를 벗기고(짝 태그는 원문 마크업으로 복원) XML 엔티티를 원복.
    private static string Unmask(string xml, Masked m, string target)
    {
        xml = PhQuoteRx.Replace(xml, "$1");       // 플레이스홀더 따옴표 제거(언어 무관)
        xml = CollapseDuplicateSpans(xml, m);     // DeepL 이 쪼갠 중복 <gN> 스팬 병합
        xml = TrimHighlightSpans(xml, m, target); // 하이라이트 정리(따옴표 공통 + 조사 KO만)
        // 뒤 번호부터 복원(<g1> 이 <g10> 안에 섞이지 않게 — '>' 로 이미 안전하지만 안전제일).
        for (int n = m.Pairs.Count - 1; n >= 0; n--)
            xml = xml.Replace($"<g{n}>", m.Pairs[n].open)
                     .Replace($"</g{n}>", m.Pairs[n].close);
        return XmlUnescape(xml.Replace("<ph>", "").Replace("</ph>", ""));
    }

    private static string XmlEscape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string XmlUnescape(string s) => s
        .Replace("&lt;", "<").Replace("&gt;", ">")
        .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
        .Replace("&amp;", "&"); // &amp; 는 반드시 마지막
}
