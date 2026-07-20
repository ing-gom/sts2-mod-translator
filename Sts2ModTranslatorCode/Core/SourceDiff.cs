using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Sts2ModTranslator.Core;

/// <summary>
/// 원문 두 버전(번역 당시 baseline vs 현재)의 <b>단어 단위</b> 차이를 사람이 읽을 수 있는 한 줄로
/// 요약한다. "원문이 바뀜"만 알리는 것보다, 어느 <b>단어</b>가 어떻게 바뀌었는지 짚어 주면 번역자가
/// 무엇을 고쳐야 하는지 바로 안다. 예: <c>Deal 5 damage.</c> → <c>Deal 8 damage.</c> = <c>Deal ⟨5→8⟩ damage.</c>
///
/// 토큰 = 공백 런/비공백 런(<c>\s+|\S+</c>). 대부분의 원문은 영어(SourceLang=eng)라 단어 단위가 잘 맞고,
/// 공백 없는 CJK 문장은 통째 한 토큰이라 문장 교체(<c>⟨옛→새⟩</c>)로 떨어진다(그래도 옛/새를 다 보여 줌).
/// 순수 알고리즘(게임 API 무관) — SoloTest 로 결정적 검증.
/// </summary>
public static class SourceDiff
{
    private static readonly Regex TokenRx = new(@"\s+|\S+", RegexOptions.Compiled);

    // 토큰 수가 이보다 많으면(비정상적으로 긴 문자열) LCS DP 비용을 피하고 통째 교체로 요약.
    private const int MaxTokens = 400;

    /// <summary>
    /// old→new 의 단어 단위 diff 를 한 줄로. 바뀐 구간은 <c>⟨옛→새⟩</c>(양쪽 존재)·<c>⟨-삭제⟩</c>·
    /// <c>⟨+추가⟩</c> 로, 그대로인 부분은 원문 그대로. <paramref name="maxLen"/> 초과 시 말줄임.
    /// 두 문자열이 같으면 빈 문자열.
    /// </summary>
    public static string Describe(string? oldS, string? newS, int maxLen = 120)
    {
        oldS ??= ""; newS ??= "";
        if (oldS == newS) return "";

        var a = Tokenize(oldS);
        var b = Tokenize(newS);
        if (a.Count > MaxTokens || b.Count > MaxTokens)
            return Clip($"“{oldS}” → “{newS}”", maxLen);

        var ops = Diff(a, b);

        var sb = new StringBuilder();
        var del = new StringBuilder();
        var ins = new StringBuilder();
        void Flush()
        {
            string d = del.ToString().Trim();
            string i = ins.ToString().Trim();
            del.Clear(); ins.Clear();
            if (d.Length == 0 && i.Length == 0) return;
            if (d.Length > 0 && i.Length > 0) sb.Append('⟨').Append(d).Append('→').Append(i).Append('⟩');
            else if (d.Length > 0) sb.Append("⟨-").Append(d).Append('⟩');
            else sb.Append("⟨+").Append(i).Append('⟩');
        }
        foreach (var (tag, tok) in ops)
        {
            if (tag == ' ') { Flush(); sb.Append(tok); }
            else if (tag == '-') del.Append(tok);
            else ins.Append(tok);
        }
        Flush();
        return Clip(sb.ToString().Trim(), maxLen);
    }

    private static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        foreach (Match m in TokenRx.Matches(s)) list.Add(m.Value);
        return list;
    }

    /// <summary>
    /// 토큰 배열의 LCS 기반 편집 스크립트. 반환은 순서대로 (' '=공통, '-'=삭제(old만), '+'=추가(new만)).
    /// 짧은 문자열용 O(n·m) DP — 카드 설명 길이(수십 토큰)에 충분.
    /// </summary>
    private static List<(char tag, string tok)> Diff(List<string> a, List<string> b)
    {
        int n = a.Count, m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1
                                         : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var ops = new List<(char, string)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y]) { ops.Add((' ', a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { ops.Add(('-', a[x])); x++; }
            else { ops.Add(('+', b[y])); y++; }
        }
        while (x < n) ops.Add(('-', a[x++]));
        while (y < m) ops.Add(('+', b[y++]));
        return ops;
    }

    private static string Clip(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
