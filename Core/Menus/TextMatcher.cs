namespace AutoPickup.Core.Menus;

/// <summary>OCR 文本清洗与容差匹配。</summary>
public static class TextMatcher
{
    /// <summary>只保留字母数字（CJK 属于字母），去空白与标点（如 OCR 的多余 “《”）。</summary>
    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>清洗后容差匹配：短串 ≤1 处差异，长串按长度比例放宽，且首尾字符一致。</summary>
    public static bool FuzzyEqual(string ocrText, string target)
    {
        string a = Clean(ocrText), b = Clean(target);
        if (a == b) return true;
        if (a.Length == 0 || b.Length == 0) return false;
        int maxDist = b.Length <= 8 ? 1 : Math.Max(1, b.Length / 4);
        if (Math.Abs(a.Length - b.Length) > maxDist + 1) return false;
        if (Levenshtein(a, b) <= maxDist) return true;
        // 首尾一致但中间有个别字误识（如 进->迸）
        if (a[0] == b[0] && a[^1] == b[^1] && Levenshtein(a, b) <= maxDist + 1) return true;
        // OCR 拆词片段（短侧 ≤2 字且被长侧包含，如 线 -> 在线 / 在 -> 在线）
        if (a.Length <= 2 && b.Contains(a)) return true;
        if (b.Length <= 2 && a.Contains(b)) return true;
        return false;
    }
}