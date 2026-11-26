using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Custos.Core.Util;
using Custos.Registry.Git;

namespace Custos.Registry.Commits;

/// <summary>署名の鍵（秘密鍵の場所と公開鍵）。秘密鍵はデータの置き場所の secrets に置き、リポジトリに入れない。</summary>
public sealed record SigningKey(string PrivateKeyPath, string PublicKeyLine, string Fingerprint);

/// <summary>署名の鍵の生成と読み込み（設計書 第20.5節、第20.9節「鍵の管理」）。</summary>
public static class SigningKeys
{
    /// <summary>
    /// ed25519 の鍵を作る。すでにあれば上書きせずに失敗する。秘密鍵は所有者だけが読めるようにする。
    /// </summary>
    public static async Task<SigningKey> GenerateAsync(string secretsDir, string name, string comment, ICommandRunner? runner = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretsDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(comment);
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            throw new CustosException($"鍵の名前に / は使えません：{name}");
        }

        Directory.CreateDirectory(secretsDir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(secretsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var path = Path.Combine(secretsDir, name);
        if (File.Exists(path) || File.Exists(path + ".pub"))
        {
            throw new CustosException($"鍵はすでにあります（上書きしません）：{path}");
        }

        runner ??= new ProcessCommandRunner();
        var result = await runner.RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", string.Empty, "-C", comment, "-f", path], secretsDir, null, cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("ssh-keygen（鍵の生成）");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return Load(path);
    }

    /// <summary>秘密鍵の場所から読む（公開鍵は同じ場所の .pub）。</summary>
    public static SigningKey Load(string privateKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);
        var publicPath = privateKeyPath + ".pub";
        if (!File.Exists(privateKeyPath) || !File.Exists(publicPath))
        {
            throw new CustosException($"署名の鍵がありません：{privateKeyPath}（custos keys init で作る）");
        }

        var line = File.ReadAllText(publicPath).Trim();
        return new SigningKey(Path.GetFullPath(privateKeyPath), line, Fingerprint(line));
    }

    /// <summary>
    /// 公開鍵（"ssh-ed25519 AAAA... comment"）の指紋（SHA256:...）。ssh-keygen -l と同じ値になる。
    /// </summary>
    public static string Fingerprint(string publicKeyLine)
    {
        ArgumentNullException.ThrowIfNull(publicKeyLine);
        var parts = publicKeyLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var blob = parts.FirstOrDefault(p => p.StartsWith("AAAA", StringComparison.Ordinal))
            ?? throw new CustosException("公開鍵を読めません：" + publicKeyLine);
        var bytes = Convert.FromBase64String(blob);
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=');
    }
}

/// <summary>鍵の役割。</summary>
public enum SignerRole
{
    /// <summary>フレームワーク（入口の検査を通ったコミットにだけ署名する）。</summary>
    Framework,

    /// <summary>登録した人間の開発者。</summary>
    Human,
}

/// <summary>許可する鍵の1件。</summary>
public sealed record AllowedSigner(
    string Principal,
    string KeyType,
    string KeyBlob,
    string Owner,
    SignerRole Role,
    DateOnly? ValidAfter,
    DateOnly? ValidBefore,
    string? Note)
{
    /// <summary>指紋。</summary>
    public string Fingerprint => SigningKeys.Fingerprint($"{KeyType} {KeyBlob}");

    /// <summary>ある日時に有効か（有効期間の両端を含む）。</summary>
    public bool IsValidAt(DateTimeOffset time)
    {
        var date = DateOnly.FromDateTime(time.UtcDateTime);
        return (ValidAfter == null || date >= ValidAfter.Value) && (ValidBefore == null || date <= ValidBefore.Value);
    }
}

/// <summary>
/// 許可する鍵の一覧（検証用リポジトリの keys/allowed_signers）。git の allowed_signers 形式で、
/// 鍵ごとの持ち主、役割、有効期間を、直前の注記行（# owner=... role=...）と valid-after / valid-before で表す。
/// 鍵を交換するときは古い鍵に終了日を付けて残す（過去のコミットの検証に要るため）。
/// </summary>
public sealed class AllowedSignersFile
{
    private readonly List<AllowedSigner> _entries;

    private AllowedSignersFile(List<AllowedSigner> entries) => _entries = entries;

    /// <summary>鍵の一覧。</summary>
    public IReadOnlyList<AllowedSigner> Entries => _entries;

    /// <summary>空の一覧。</summary>
    public static AllowedSignersFile Empty() => new([]);

    /// <summary>ファイルを読む。なければ空の一覧。</summary>
    public static AllowedSignersFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : Empty();
    }

    /// <summary>内容を読む。</summary>
    public static AllowedSignersFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<AllowedSigner>();
        Dictionary<string, string>? pending = null;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                pending = null;
                continue;
            }

            if (line.StartsWith('#'))
            {
                var meta = ParseMeta(line[1..]);
                pending = meta.ContainsKey("owner") || meta.ContainsKey("role") ? meta : pending;
                continue;
            }

            entries.Add(ParseEntry(line, pending));
            pending = null;
        }

        return new AllowedSignersFile(entries);
    }

    private static Dictionary<string, string> ParseMeta(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                meta[token[..eq]] = token[(eq + 1)..];
            }
        }

        return meta;
    }

    private static AllowedSigner ParseEntry(string line, Dictionary<string, string>? meta)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3)
        {
            throw new CustosException("allowed_signers の行を読めません：" + line);
        }

        var principal = tokens[0];
        var index = 1;
        string? options = null;
        if (!IsKeyType(tokens[1]))
        {
            options = tokens[1];
            index = 2;
        }

        if (index + 1 >= tokens.Length)
        {
            throw new CustosException("allowed_signers の行に鍵がありません：" + line);
        }

        var keyType = tokens[index];
        var blob = tokens[index + 1];
        var note = index + 2 < tokens.Length ? string.Join(' ', tokens[(index + 2)..]) : null;
        DateOnly? validAfter = null;
        DateOnly? validBefore = null;
        if (options != null)
        {
            foreach (var option in SplitOptions(options))
            {
                var eq = option.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0)
                {
                    continue;
                }

                var key = option[..eq];
                var value = option[(eq + 1)..].Trim('"');
                if (key == "valid-after")
                {
                    validAfter = ParseDate(value);
                }
                else if (key == "valid-before")
                {
                    // ファイルには「その日時より前まで有効」で書くので、最後に有効な日は前日
                    validBefore = ParseDate(value).AddDays(-1);
                }
            }
        }

        var owner = meta != null && meta.TryGetValue("owner", out var o) ? o : principal;
        var role = meta != null && meta.TryGetValue("role", out var r) && r == "human" ? SignerRole.Human : SignerRole.Framework;
        if (meta == null || !meta.ContainsKey("role"))
        {
            // 役割の注記のない鍵は、厳しい側に倒して人間の鍵として扱う（フレームワークの署名とはみなさない）
            role = SignerRole.Human;
        }

        return new AllowedSigner(principal, keyType, blob, owner, role, validAfter, validBefore, note);
    }

    private static IEnumerable<string> SplitOptions(string options)
    {
        var sb = new StringBuilder();
        var quoted = false;
        foreach (var c in options)
        {
            if (c == '"')
            {
                quoted = !quoted;
                sb.Append(c);
            }
            else if (c == ',' && !quoted)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            yield return sb.ToString();
        }
    }

    private static bool IsKeyType(string token) =>
        token.StartsWith("ssh-", StringComparison.Ordinal) || token.StartsWith("ecdsa-", StringComparison.Ordinal) || token.StartsWith("sk-", StringComparison.Ordinal);

    private static DateOnly ParseDate(string value)
    {
        var digits = value.Length >= 8 ? value[..8] : value;
        return DateOnly.ParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture);
    }

    /// <summary>指紋で引く。</summary>
    public AllowedSigner? FindByFingerprint(string fingerprint) =>
        _entries.FirstOrDefault(e => string.Equals(e.Fingerprint, fingerprint, StringComparison.Ordinal));

    /// <summary>鍵を足す。同じ鍵があれば失敗する。</summary>
    public void Add(AllowedSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (signer.Principal.Contains(' ', StringComparison.Ordinal))
        {
            throw new CustosException("principal に空白は使えません：" + signer.Principal);
        }

        if (FindByFingerprint(signer.Fingerprint) != null)
        {
            throw new CustosException("その鍵はすでに一覧にあります：" + signer.Fingerprint);
        }

        _entries.Add(signer);
    }

    /// <summary>公開鍵の行から足す。</summary>
    public AllowedSigner Add(string principal, string publicKeyLine, string owner, SignerRole role, DateOnly? validAfter, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(publicKeyLine);
        var parts = publicKeyLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new CustosException("公開鍵を読めません：" + publicKeyLine);
        }

        var signer = new AllowedSigner(principal, parts[0], parts[1], owner, role, validAfter, null, note ?? (parts.Length > 2 ? string.Join(' ', parts[2..]) : null));
        Add(signer);
        return signer;
    }

    /// <summary>鍵を交換するときに、古い鍵に終了日を付ける（一覧には残す）。</summary>
    public AllowedSigner Retire(string fingerprint, DateOnly lastValidDate)
    {
        var index = _entries.FindIndex(e => string.Equals(e.Fingerprint, fingerprint, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new CustosException("一覧にない鍵です：" + fingerprint);
        }

        _entries[index] = _entries[index] with { ValidBefore = lastValidDate };
        return _entries[index];
    }

    /// <summary>
    /// 漏れた鍵を一覧から外す。外した鍵を返すので、その鍵で署名された該当期間のコミットを確認し直す（第20.9節）。
    /// </summary>
    public AllowedSigner Remove(string fingerprint)
    {
        var index = _entries.FindIndex(e => string.Equals(e.Fingerprint, fingerprint, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new CustosException("一覧にない鍵です：" + fingerprint);
        }

        var removed = _entries[index];
        _entries.RemoveAt(index);
        return removed;
    }

    /// <summary>ファイルに書く。</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, Render(), new UTF8Encoding(false));
    }

    /// <summary>git の allowed_signers 形式の文字列にする。</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("# Custos：許可する鍵の一覧（設計書 第20.9節）。公開鍵だけを置く。秘密鍵はここに置かない。\n");
        sb.Append("# 各鍵の直前の行に、持ち主（owner）と役割（role=framework / human）を書く。\n\n");
        foreach (var entry in _entries)
        {
            sb.Append("# owner=").Append(entry.Owner).Append(" role=").Append(entry.Role == SignerRole.Framework ? "framework" : "human").Append('\n');
            var options = new List<string> { "namespaces=\"git\"" };
            if (entry.ValidAfter is { } after)
            {
                options.Add($"valid-after=\"{after.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}\"");
            }

            if (entry.ValidBefore is { } before)
            {
                // valid-before は、その日時より前まで有効。終了日の終わりまで有効にするため翌日を書く
                options.Add($"valid-before=\"{before.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}\"");
            }

            sb.Append(entry.Principal).Append(' ').Append(string.Join(',', options)).Append(' ').Append(entry.KeyType).Append(' ').Append(entry.KeyBlob);
            if (!string.IsNullOrWhiteSpace(entry.Note))
            {
                sb.Append(' ').Append(entry.Note);
            }

            sb.Append("\n\n");
        }

        return sb.ToString();
    }
}

/// <summary>コミットの署名の信頼の度合い。</summary>
public enum SignatureTrust
{
    /// <summary>フレームワークの鍵で正しく署名されている。</summary>
    Framework,

    /// <summary>登録した人間の鍵で正しく署名されている。</summary>
    Human,

    /// <summary>署名がない。</summary>
    NotSigned,

    /// <summary>署名はあるが、許可していない鍵か、有効期間の外。</summary>
    UnknownKey,

    /// <summary>署名が壊れている。</summary>
    Bad,

    /// <summary>検証できなかった。</summary>
    Error,
}

/// <summary>署名を、許可する鍵の一覧に照らして分類する。</summary>
public static class SignaturePolicy
{
    /// <summary>git log の %G? と %GK（指紋）から分類する。</summary>
    public static SignatureTrust Classify(CommitInfo commit, AllowedSignersFile allowed)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(allowed);
        switch (commit.SignatureStatus)
        {
            case 'G':
                var signer = allowed.FindByFingerprint(commit.KeyFingerprint);
                if (signer == null)
                {
                    return SignatureTrust.UnknownKey;
                }

                return signer.Role == SignerRole.Framework ? SignatureTrust.Framework : SignatureTrust.Human;
            case 'N':
                return SignatureTrust.NotSigned;
            case 'U' or 'X' or 'Y' or 'R':
                return SignatureTrust.UnknownKey;
            case 'B':
                return SignatureTrust.Bad;
            default:
                return SignatureTrust.Error;
        }
    }

    /// <summary>信頼できる署名か（フレームワークか登録した人間）。</summary>
    public static bool IsTrusted(SignatureTrust trust) => trust is SignatureTrust.Framework or SignatureTrust.Human;

    /// <summary>表示用の説明。</summary>
    public static string Describe(SignatureTrust trust) => trust switch
    {
        SignatureTrust.Framework => "フレームワークの署名",
        SignatureTrust.Human => "登録した人間の署名",
        SignatureTrust.NotSigned => "署名がない",
        SignatureTrust.UnknownKey => "許可していない鍵の署名（または有効期間の外）",
        SignatureTrust.Bad => "署名が壊れている",
        _ => "署名を検証できなかった",
    };
}
