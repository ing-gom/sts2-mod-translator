using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sts2ModTranslator.Core;

/// <summary>
/// 로컬라이제이션 JSON 읽기 공통 정책.
///
/// 게임 로더는 주석(<c>//</c>, <c>/* */</c>)이 든 로크 파일도 받아들인다 — 모더가 키 옆에 메모를
/// 남기는 흔한 패턴이다. 우리가 strict 로 읽으면 <see cref="JsonException"/> 이 나고, 호출부의
/// best-effort catch 가 빈 dict 를 돌려주므로 <b>그 테이블 전체가 조용히 사라진다</b>
/// (사용자에겐 "이 모드의 이 테이블만 키가 하나도 안 잡힘" 으로 보이고 원인 단서가 없음).
/// 그래서 읽기는 게임과 같은 관용도로 맞춘다. 후행 콤마도 함께 허용 — 주석을 쓰는 손편집
/// 파일에서 같이 나오는 경우가 많다.
///
/// <b>쓰기</b>는 여전히 strict JSON(주석 없음)으로 내보낸다 — 각 store 의 WriteOpts 참조.
/// </summary>
internal static class LocJson
{
    /// <summary>모든 로크/매니페스트 <b>읽기</b>에 쓰는 파서 옵션.</summary>
    public static readonly JsonSerializerOptions Read = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>이미 경고한 경로(세션당 1회만 — 스캔은 부팅/새로고침마다 여러 번 돈다).</summary>
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>모드 id → 파싱에 실패한 파일 수(중복 제외). 미지원 사유 표시에 쓴다.</summary>
    private static readonly Dictionary<string, int> FailuresById = new(StringComparer.Ordinal);

    /// <summary>
    /// 로크 테이블 파싱 실패를 모드 id + 파일 경로 + 예외와 함께 경고한다.
    /// 같은 경로는 세션당 한 번만 찍는다(스캔 반복으로 로그가 불지 않게).
    /// </summary>
    public static void WarnParseFailure(string path, string modId, Exception ex)
    {
        string id = string.IsNullOrEmpty(modId) ? GuessModId(path) : modId;
        lock (Warned)
        {
            if (!Warned.Add(path)) return;
            FailuresById[id] = FailuresById.TryGetValue(id, out int n) ? n + 1 : 1;
        }
        MainFile.Logger.Warn(
            "[Sts2ModTranslator] Localization table skipped — could not parse JSON. "
          + $"mod='{id}' file='{path}' :: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>이 모드에서 파싱에 실패한 로크 파일 수(0 이면 실패 없음).</summary>
    public static int FailureCount(string modId)
    {
        lock (Warned) return FailuresById.TryGetValue(modId, out int n) ? n : 0;
    }

    /// <summary>res://{id}/... 또는 .../mods/{id}/... 경로에서 모드 id 를 추정(로그 표시용).</summary>
    private static string GuessModId(string path)
    {
        try
        {
            const string res = "res://";
            if (path.StartsWith(res, StringComparison.Ordinal))
            {
                string rest = path.Substring(res.Length);
                int slash = rest.IndexOf('/');
                return slash > 0 ? rest.Substring(0, slash) : rest;
            }
            var parts = path.Replace('\\', '/').Split('/');
            // .../{modId}/localization/... 또는 .../{modId}/translations/...
            for (int i = parts.Length - 1; i > 0; i--)
            {
                if (parts[i].Equals("localization", StringComparison.OrdinalIgnoreCase)
                 || parts[i].Equals("translations", StringComparison.OrdinalIgnoreCase))
                    return parts[i - 1];
            }
        }
        catch { /* 표시용이므로 실패해도 무시 */ }
        return "?";
    }

    // ── 편집기 JSON 오류 리포트 ────────────────────────────────
    // 번역 편집기는 생 JSON 을 그대로 보여 준다. 문법이 깨지면 System.Text.Json 의 예외는
    // 0-based 줄 번호와 *바이트* 오프셋을 준다 — 편집기 행 번호(1-based)/문자 컬럼과 안 맞고,
    // CJK 본문에서는 컬럼이 3배로 어긋나 사용자가 깨진 자리를 못 찾는다. 여기서 사람 좌표로 환산한다.

    /// <summary>편집기에 보여 줄 JSON 오류 — 영어 메시지 + 1-based 줄/문자 컬럼(0 = 위치 불명).</summary>
    public readonly struct EditError
    {
        public readonly string Message;
        public readonly int Line;
        public readonly int Column;
        public EditError(string message, int line, int column)
        { Message = message; Line = line; Column = column; }

        /// <summary>"line 2, column 60: …" 형태의 한 줄 요약(위치를 모르면 메시지만).</summary>
        public string Describe() =>
            Line > 0 ? $"line {Line}, column {Column}: {Message}" : Message;
    }

    /// <summary>예외를 편집기 좌표(1-based 줄/문자)로 환산한다. JSON 예외가 아니면 메시지만.</summary>
    public static EditError ToEditError(Exception ex, string text)
    {
        if (ex is not JsonException je || je.LineNumber is null) return new EditError(ex.Message, 0, 0);

        int line0 = (int)je.LineNumber.Value;
        long bytePos = je.BytePositionInLine ?? 0;
        string lineText = LineAt(text, line0);
        // 바이트 오프셋 → 문자 컬럼. CJK 는 UTF-8 3바이트이므로 단순 +1 로는 못 맞춘다.
        int col = 0; long b = 0;
        while (col < lineText.Length && b < bytePos)
        {
            b += Encoding.UTF8.GetByteCount(lineText, col, char.IsHighSurrogate(lineText[col]) && col + 1 < lineText.Length ? 2 : 1);
            col += char.IsHighSurrogate(lineText[col]) && col + 1 < lineText.Length ? 2 : 1;
        }
        return new EditError(Humanize(je.Message), line0 + 1, col + 1);
    }

    /// <summary>text 의 0-based line0 번째 줄(없으면 "").</summary>
    public static string LineAt(string text, int line0)
    {
        if (line0 < 0) return "";
        int start = 0;
        for (int i = 0; i < line0; i++)
        {
            int nl = text.IndexOf('\n', start);
            if (nl < 0) return "";
            start = nl + 1;
        }
        int end = text.IndexOf('\n', start);
        string s = end < 0 ? text.Substring(start) : text.Substring(start, end - start);
        return s.TrimEnd('\r');
    }

    /// <summary>.NET JSON 메시지에서 중복 좌표 꼬리(" | LineNumber: … ")를 떼어 낸다.</summary>
    private static string Humanize(string msg)
    {
        int bar = msg.IndexOf(" | LineNumber:", StringComparison.Ordinal);
        return (bar > 0 ? msg.Substring(0, bar) : msg).Trim();
    }

    // ── "따옴표 밖에 붙여 넣음" 자동 복구 ──────────────────────
    // 압도적으로 흔한 깨짐 방식: 번역문을 값의 "" *사이*가 아니라 뒤에 붙여 넣어
    //   "KEY": "",번역문",
    // 이 되는 것. 키는 멀쩡하므로 값만 다시 감싸면 복구된다.

    /// <summary>줄 앞부분에서 `  "KEY":` 를 떼어 낸다(들여쓰기, 키, 나머지).</summary>
    private static readonly Regex EntryHeadRx =
        new(@"^(\s*)""((?:[^""\\]|\\.)+)""\s*:\s*(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// 한 줄을 `  "KEY": "값",` 형태로 다시 조립한다. 값은 바깥쪽의 군더더기
    /// (따옴표·콤마·공백)를 털어 내고 내부의 맨 따옴표만 이스케이프한다.
    /// 키를 못 찾으면 null(복구 불가).
    /// </summary>
    public static string? RepairEntryLine(string line)
    {
        var m = EntryHeadRx.Match(line);
        if (!m.Success) return null;
        string indent = m.Groups[1].Value, key = m.Groups[2].Value, rest = m.Groups[3].Value;

        bool comma = rest.TrimEnd().EndsWith(",", StringComparison.Ordinal);
        // 값 바깥의 구조 문자만 제거 — 전각 ，。 등은 본문이므로 건드리지 않는다.
        char[] noise = { '"', ',', ' ', '\t' };
        string value = rest.Trim(noise);
        return $"{indent}\"{key}\": \"{EscapeInner(value)}\"{(comma ? "," : "")}";
    }

    /// <summary>이미 이스케이프된 시퀀스(\n 등)는 보존하고, 맨 " 와 끝의 홀 \ 만 고친다.</summary>
    private static string EscapeInner(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\')
            {
                if (i + 1 < s.Length) { sb.Append(c).Append(s[i + 1]); i++; }
                else sb.Append("\\\\"); // 줄 끝의 홀 백슬래시
            }
            else if (c == '"') sb.Append("\\\"");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> 의 line0 번째 줄만 복구해 본 전체 텍스트를 돌려준다.
    /// 복구본이 실제로 파싱되지 않으면 null — 추측으로 사용자 원고를 망치지 않는다.
    /// </summary>
    public static (string fixedText, string fixedLine)? TryRepair(string text, int line0)
    {
        string bad = LineAt(text, line0);
        if (bad.Length == 0) return null;
        string? good = RepairEntryLine(bad);
        if (good == null || good == bad) return null;

        var lines = text.Split('\n');
        if (line0 < 0 || line0 >= lines.Length) return null;
        string eol = lines[line0].EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
        lines[line0] = good + eol;
        string candidate = string.Join("\n", lines);

        try { JsonSerializer.Deserialize<Dictionary<string, string>>(candidate, Read); }
        catch { return null; } // 이 줄 말고도 깨진 데가 있다 → 자동 복구 사절
        return (candidate, good);
    }
}
