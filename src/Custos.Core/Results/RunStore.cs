using System.Globalization;
using System.Text;
using Custos.Core.Util;

namespace Custos.Core.Results;

/// <summary>1回の実行の付帯情報（設計書 第12.6節「結果を改ざんできなくする」、第17.4節）。</summary>
public sealed record RunMetadata(
    string Id,
    int Number,
    string Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    string FrameworkVersion,
    string JudgeHash,
    string? ConfigHash,
    IReadOnlyDictionary<string, string> ConfigFileHashes,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>封をした実行の、改ざん検知用の連鎖の1行。</summary>
public sealed record LedgerLine(string RunId, string ManifestHash, string PreviousHash, string LineHash, DateTimeOffset SealedAt);

/// <summary>
/// 結果の保存先。実行ごとに番号を振り、フレームワークの版、判定器のハッシュ、設定のハッシュを記録する。
/// 書いたファイルは上書きできない（追記のみ）。封をすると、全ファイルのハッシュを連鎖に足し、読み取り専用にする。
/// </summary>
public sealed class RunStore
{
    private readonly string _root;

    /// <summary>保存先のディレクトリを指定して作る。</summary>
    public RunStore(string resultsDir)
    {
        _root = resultsDir;
        Directory.CreateDirectory(Path.Combine(_root, "runs"));
    }

    /// <summary>保存先のディレクトリ。</summary>
    public string Root => _root;

    /// <summary>連鎖のファイル。</summary>
    public string LedgerPath => Path.Combine(_root, "ledger.jsonl");

    /// <summary>新しい実行を始める。番号はプロセスをまたいで一意に振る。</summary>
    public Run Begin(string kind, string? configHash, IReadOnlyDictionary<string, string>? configFileHashes, IReadOnlyDictionary<string, string>? labels = null)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var number = NextNumber();
        var id = $"{number.ToString("D6", CultureInfo.InvariantCulture)}-{kind}";
        var dir = Path.Combine(_root, "runs", id);
        Directory.CreateDirectory(dir);
        var metadata = new RunMetadata(
            id,
            number,
            kind,
            DateTimeOffset.UtcNow,
            null,
            "running",
            FrameworkInfo.Version,
            FrameworkInfo.JudgeHash,
            configHash,
            configFileHashes ?? new Dictionary<string, string>(),
            labels ?? new Dictionary<string, string>());
        var run = new Run(this, dir, metadata);
        run.WriteMetadata();
        return run;
    }

    private int NextNumber()
    {
        var lockPath = Path.Combine(_root, "counter.lock");
        var counterPath = Path.Combine(_root, "counter");
        for (var attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                using var guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var current = File.Exists(counterPath)
                    ? int.Parse(File.ReadAllText(counterPath).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture)
                    : 0;
                var next = current + 1;
                File.WriteAllText(counterPath, next.ToString(CultureInfo.InvariantCulture));
                return next;
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }

        throw new CustosException("結果の番号を振れませんでした（counter.lock を確かめてください）");
    }

    /// <summary>封をした実行を連鎖に足す。</summary>
    internal LedgerLine AppendLedger(string runId, string manifestHash)
    {
        var lockPath = Path.Combine(_root, "ledger.lock");
        for (var attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                using var guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var previous = "genesis";
                if (File.Exists(LedgerPath))
                {
                    var last = File.ReadLines(LedgerPath).LastOrDefault(l => l.Length > 0);
                    if (last != null)
                    {
                        previous = Json.Deserialize<LedgerLine>(last).LineHash;
                    }
                }

                var sealedAt = DateTimeOffset.UtcNow;
                var lineHash = Hashing.Sha256Hex($"{previous}\n{runId}\n{manifestHash}\n{sealedAt:O}");
                var line = new LedgerLine(runId, manifestHash, previous, lineHash, sealedAt);
                File.AppendAllText(LedgerPath, Json.SerializeCompact(line) + "\n");
                return line;
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }

        throw new CustosException("結果の連鎖に書き込めませんでした（ledger.lock を確かめてください）");
    }

    /// <summary>
    /// 連鎖とファイルのハッシュを確かめ、改ざんを検知する。問題の一覧を返す（空なら問題なし）。
    /// </summary>
    public IReadOnlyList<string> Verify()
    {
        var problems = new List<string>();
        if (!File.Exists(LedgerPath))
        {
            return problems;
        }

        var previous = "genesis";
        foreach (var text in File.ReadLines(LedgerPath).Where(l => l.Length > 0))
        {
            var line = Json.Deserialize<LedgerLine>(text);
            if (line.PreviousHash != previous)
            {
                problems.Add($"{line.RunId}: 連鎖が途切れています（前の行のハッシュと合わない）");
            }

            var expected = Hashing.Sha256Hex($"{line.PreviousHash}\n{line.RunId}\n{line.ManifestHash}\n{line.SealedAt:O}");
            if (expected != line.LineHash)
            {
                problems.Add($"{line.RunId}: 連鎖の行が書き換えられています");
            }

            var dir = Path.Combine(_root, "runs", line.RunId);
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                problems.Add($"{line.RunId}: manifest.json がありません");
            }
            else
            {
                var manifestText = File.ReadAllText(manifestPath);
                if (Hashing.Sha256Hex(manifestText) != line.ManifestHash)
                {
                    problems.Add($"{line.RunId}: manifest.json が書き換えられています");
                }

                var manifest = Json.Deserialize<Dictionary<string, string>>(manifestText);
                foreach (var pair in manifest)
                {
                    var file = Path.Combine(dir, pair.Key);
                    if (!File.Exists(file))
                    {
                        problems.Add($"{line.RunId}: {pair.Key} がありません");
                    }
                    else if (Hashing.Sha256File(file) != pair.Value)
                    {
                        problems.Add($"{line.RunId}: {pair.Key} が書き換えられています");
                    }
                }
            }

            previous = line.LineHash;
        }

        return problems;
    }

    /// <summary>実行の一覧（新しい順）。</summary>
    public IReadOnlyList<RunMetadata> List()
    {
        var runs = new List<RunMetadata>();
        var dir = Path.Combine(_root, "runs");
        foreach (var runDir in Directory.EnumerateDirectories(dir))
        {
            var meta = Path.Combine(runDir, "run.json");
            if (File.Exists(meta))
            {
                runs.Add(Json.Deserialize<RunMetadata>(File.ReadAllText(meta)));
            }
        }

        return runs.OrderByDescending(r => r.Number).ToList();
    }
}

/// <summary>1回の実行。ここに書いたファイルが、その実行の根拠（証拠）になる。</summary>
public sealed class Run
{
    private readonly RunStore _store;
    private readonly object _gate = new();
    private RunMetadata _metadata;
    private bool _sealed;

    internal Run(RunStore store, string dir, RunMetadata metadata)
    {
        _store = store;
        Dir = dir;
        _metadata = metadata;
    }

    /// <summary>実行のディレクトリ。</summary>
    public string Dir { get; }

    /// <summary>実行の ID（番号と種類）。</summary>
    public string Id => _metadata.Id;

    /// <summary>付帯情報。</summary>
    public RunMetadata Metadata => _metadata;

    /// <summary>実行のディレクトリからの相対パスを絶対パスにする。</summary>
    public string PathOf(string relative) => Path.Combine(Dir, relative);

    /// <summary>ラベル（アプリのコミット、プロファイル名など）を足す。</summary>
    public void Label(string key, string value)
    {
        lock (_gate)
        {
            var labels = new Dictionary<string, string>(_metadata.Labels, StringComparer.Ordinal) { [key] = value };
            _metadata = _metadata with { Labels = labels };
            WriteMetadata();
        }
    }

    /// <summary>テキストを書く。同じ名前のファイルがあれば失敗する（上書きしない）。</summary>
    public string WriteText(string relative, string text)
    {
        var path = PrepareNew(relative);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    /// <summary>JSON を書く。</summary>
    public string WriteJson<T>(string relative, T value) => WriteText(relative, Json.Serialize(value));

    /// <summary>バイト列を書く。</summary>
    public string WriteBytes(string relative, byte[] data)
    {
        var path = PrepareNew(relative);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>ファイルを複製して証拠に加える。</summary>
    public string CopyIn(string source, string relative)
    {
        var path = PrepareNew(relative);
        File.Copy(source, path);
        return path;
    }

    /// <summary>1行の JSON を追記する（実行中のログ）。</summary>
    public void AppendJsonLine<T>(string relative, T value)
    {
        lock (_gate)
        {
            EnsureOpen();
            var path = PathOf(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Dir);
            File.AppendAllText(path, Json.SerializeCompact(value) + "\n");
        }
    }

    private string PrepareNew(string relative)
    {
        lock (_gate)
        {
            EnsureOpen();
            var path = PathOf(relative);
            if (File.Exists(path))
            {
                throw new CustosException($"結果のファイルはすでにあります（上書きしません）：{relative}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Dir);
            return path;
        }
    }

    private void EnsureOpen()
    {
        if (_sealed)
        {
            throw new CustosException($"実行 {Id} はすでに封をしています。追記できません");
        }
    }

    internal void WriteMetadata()
    {
        File.WriteAllText(PathOf("run.json"), Json.Serialize(_metadata));
    }

    /// <summary>
    /// 封をする。全ファイルのハッシュを manifest.json に書き、連鎖に足し、読み取り専用にする。
    /// </summary>
    public LedgerLine Seal(string status)
    {
        lock (_gate)
        {
            EnsureOpen();
            _metadata = _metadata with { Status = status, FinishedAt = DateTimeOffset.UtcNow };
            WriteMetadata();
            var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Dir, file).Replace('\\', '/');
                if (relative == "manifest.json")
                {
                    continue;
                }

                manifest[relative] = Hashing.Sha256File(file);
            }

            var manifestText = Json.Serialize(manifest);
            File.WriteAllText(PathOf("manifest.json"), manifestText);
            var line = _store.AppendLedger(Id, Hashing.Sha256Hex(manifestText));
            _sealed = true;
            if (!OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
            }

            return line;
        }
    }
}
