using System;
using System.Collections.Generic;
using System.Text.Json;

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
}
