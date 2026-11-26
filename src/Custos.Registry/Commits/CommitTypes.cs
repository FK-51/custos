using System.Text;

namespace Custos.Registry.Commits;

/// <summary>コミットの種類（設計書 第20.3節）。</summary>
public enum CommitType
{
    /// <summary>機能追加。</summary>
    Feat,

    /// <summary>バグ修正。バグIDと回帰テストが必須。</summary>
    Fix,

    /// <summary>挙動を変えない整理。判定結果が変わっていないこと。</summary>
    Refactor,

    /// <summary>テストの追加・変更。</summary>
    Test,

    /// <summary>その他。</summary>
    Chore,
}

/// <summary>種類の名前の読み書き。</summary>
public static class CommitTypes
{
    /// <summary>使える名前。</summary>
    public static readonly IReadOnlyList<string> Names = ["feat", "fix", "refactor", "test", "chore"];

    /// <summary>名前を読む。</summary>
    public static bool TryParse(string? text, out CommitType type)
    {
        type = CommitType.Chore;
        switch (text?.Trim())
        {
            case "feat":
                type = CommitType.Feat;
                return true;
            case "fix":
                type = CommitType.Fix;
                return true;
            case "refactor":
                type = CommitType.Refactor;
                return true;
            case "test":
                type = CommitType.Test;
                return true;
            case "chore":
                type = CommitType.Chore;
                return true;
            default:
                return false;
        }
    }

    /// <summary>名前にする。</summary>
    public static string Name(CommitType type) => type switch
    {
        CommitType.Feat => "feat",
        CommitType.Fix => "fix",
        CommitType.Refactor => "refactor",
        CommitType.Test => "test",
        _ => "chore",
    };
}

/// <summary>コミットメッセージの末尾の分類（Type: と Bug:）の読み書き（第20.3節）。</summary>
public static class CommitTrailers
{
    /// <summary>種類のトレーラーのキー。</summary>
    public const string TypeKey = "Type";

    /// <summary>バグIDのトレーラーのキー。</summary>
    public const string BugKey = "Bug";

    /// <summary>
    /// 最終的なメッセージを作る。件名に「種類: 」が付いていなければ付ける。
    /// 手で書かれた Type: と Bug: の行は取り除き、フレームワークが決めた値を末尾に書き込む。
    /// </summary>
    public static string Compose(string message, CommitType type, string? bugId)
    {
        ArgumentNullException.ThrowIfNull(message);
        var name = CommitTypes.Name(type);
        var lines = message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        lines.RemoveAll(l => IsTrailer(l, TypeKey) || IsTrailer(l, BugKey));
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        var subject = lines[0].Trim();
        if (!HasTypePrefix(subject, name))
        {
            subject = $"{name}: {subject}";
        }

        lines[0] = subject;
        var sb = new StringBuilder();
        sb.Append(string.Join('\n', lines));
        sb.Append("\n\n");
        sb.Append(TypeKey).Append(": ").Append(name).Append('\n');
        if (bugId != null)
        {
            sb.Append(BugKey).Append(": ").Append(bugId).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>件名が「種類: 」か「種類(範囲): 」で始まっているか。</summary>
    public static bool HasTypePrefix(string subject, string typeName)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(typeName);
        if (subject.StartsWith(typeName + ": ", StringComparison.Ordinal) || subject.StartsWith(typeName + ":", StringComparison.Ordinal))
        {
            return true;
        }

        if (subject.StartsWith(typeName + "(", StringComparison.Ordinal))
        {
            var close = subject.IndexOf("):", StringComparison.Ordinal);
            return close > typeName.Length;
        }

        return false;
    }

    /// <summary>メッセージの最後の段落から Type: と Bug: を読む。</summary>
    public static (string? Type, string? Bug) Parse(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var paragraphs = message.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split("\n\n");
        if (paragraphs.Length < 2)
        {
            return (null, null);
        }

        string? type = null;
        string? bug = null;
        foreach (var line in paragraphs[^1].Split('\n'))
        {
            if (TryValue(line, TypeKey, out var t))
            {
                type = t;
            }
            else if (TryValue(line, BugKey, out var b))
            {
                bug = b;
            }
        }

        return (type, bug);
    }

    private static bool IsTrailer(string line, string key) => TryValue(line, key, out _);

    private static bool TryValue(string line, string key, out string value)
    {
        value = string.Empty;
        var prefix = key + ":";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        value = line[prefix.Length..].Trim();
        return value.Length > 0;
    }
}
