using System.Globalization;
using System.Text;
using Custos.Core.Util;
using Custos.Registry.Git;

namespace Custos.Registry.Commits;

/// <summary>取り込みの記録の1件（フレームワークが取り込んだマージコミット）。</summary>
public sealed record MergeRecord(string Commit, DateTimeOffset MergedAt, string By, string? PullRequest = null, string? BaseRef = null, string? HeadRef = null);

/// <summary>
/// 取り込みの記録（データの置き場所の merges.jsonl）。取り込みはフレームワーク自身が行うので、
/// できたマージコミットをここに記録し、チェックポイントの検査では把握済みとして扱う（設計書 第20.9節）。
/// </summary>
public sealed class MergeRecords
{
    private readonly string _path;

    /// <summary>記録のファイルを指定して作る。</summary>
    public MergeRecords(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>記録を足す（追記のみ）。</summary>
    public void Add(MergeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
        }

        File.AppendAllText(_path, Json.SerializeCompact(record) + "\n", new UTF8Encoding(false));
    }

    /// <summary>すべての記録。</summary>
    public IReadOnlyList<MergeRecord> All()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        return File.ReadLines(_path).Where(l => l.Trim().Length > 0).Select(Json.Deserialize<MergeRecord>).ToList();
    }

    /// <summary>そのコミットが取り込みの記録にあるか。</summary>
    public bool Contains(string commit) => All().Any(r => string.Equals(r.Commit, commit, StringComparison.Ordinal));
}

/// <summary>チェックポイントの検査の判定（設計書 第20.5節の表）。</summary>
public enum CheckpointState
{
    /// <summary>まだチェックポイントがない。</summary>
    NotInitialized,

    /// <summary>現在の位置がチェックポイントと同じ（問題なし）。</summary>
    Same,

    /// <summary>チェックポイント以降のコミットが、すべてフレームワークか登録済みの人間の署名付き、または取り込みの記録にある（問題なし）。</summary>
    AdvancedKnown,

    /// <summary>署名のないコミットがある（エラー：直接コミットされた）。</summary>
    UnsignedCommits,

    /// <summary>チェックポイントが現在の履歴に含まれていない（エラー：履歴が書き換えられた）。</summary>
    HistoryRewritten,
}

/// <summary>把握していないコミット1件。</summary>
public sealed record CommitFinding(string Hash, string Subject, string Reason);

/// <summary>チェックポイントの検査の結果。</summary>
public sealed record CheckpointReport(
    CheckpointState State,
    string? Checkpoint,
    string? Head,
    IReadOnlyList<CommitFinding> Unknown,
    bool HasWorkingChanges,
    string Message)
{
    /// <summary>問題がないか（作業中の変更は問題にしない）。</summary>
    public bool Ok => State is CheckpointState.NotInitialized or CheckpointState.Same or CheckpointState.AdvancedKnown;
}

/// <summary>復帰の結果。</summary>
public sealed record RecoveryResult(bool Done, string? Checkpoint, IReadOnlyList<string> UndoneCommits, string Message);

/// <summary>チェックポイントの保存（データの置き場所。リポジトリの外）。</summary>
public sealed class CheckpointStore
{
    private readonly string _dir;

    /// <summary>保存先のディレクトリを指定して作る。</summary>
    public CheckpointStore(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        _dir = dir;
    }

    private sealed record Entry(string Repo, string Head, DateTimeOffset SavedAt);

    private string FileOf(string repoPath)
    {
        var key = Hashing.Sha256Hex(Path.GetFullPath(repoPath).TrimEnd('/'))[..16];
        return Path.Combine(_dir, key + ".json");
    }

    /// <summary>リポジトリのチェックポイント（なければ null）。</summary>
    public string? Load(string repoPath)
    {
        var file = FileOf(repoPath);
        return File.Exists(file) ? Json.Deserialize<Entry>(File.ReadAllText(file)).Head : null;
    }

    /// <summary>チェックポイントを保存する。</summary>
    public void Save(string repoPath, string head)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(head);
        Directory.CreateDirectory(_dir);
        var file = FileOf(repoPath);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, Json.Serialize(new Entry(Path.GetFullPath(repoPath), head, DateTimeOffset.UtcNow)));
        File.Move(temporary, file, overwrite: true);
    }
}

/// <summary>
/// チェックポイントの検査と復帰（設計書 第20.5節）。コミットのたびに、チェックポイント以降の変化が
/// 把握済みのものかを確かめ、直接コミットされたことや履歴の書き換えを、次の操作の時点で検知する。
/// </summary>
public sealed class CheckpointGuard
{
    private readonly CheckpointStore _store;
    private readonly MergeRecords _merges;
    private readonly string _allowedSignersPath;

    /// <summary>保存先、取り込みの記録、許可する鍵の一覧を指定して作る。</summary>
    public CheckpointGuard(CheckpointStore store, MergeRecords merges, string allowedSignersPath)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(merges);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedSignersPath);
        _store = store;
        _merges = merges;
        _allowedSignersPath = allowedSignersPath;
    }

    /// <summary>検査する。</summary>
    public async Task<CheckpointReport> CheckAsync(GitClient git, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        var head = await git.HeadAsync(cancellationToken).ConfigureAwait(false);
        var working = await git.HasWorkingChangesAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = _store.Load(git.RepoPath);
        if (checkpoint == null)
        {
            return new CheckpointReport(CheckpointState.NotInitialized, null, head, [], working, "チェックポイントがまだありません");
        }

        if (head == checkpoint)
        {
            return new CheckpointReport(CheckpointState.Same, checkpoint, head, [], working, "現在の位置はチェックポイントと同じです" + WorkingNote(working));
        }

        if (head == null || !await git.CommitExistsAsync(checkpoint, cancellationToken).ConfigureAwait(false)
            || !await git.IsAncestorAsync(checkpoint, head, cancellationToken).ConfigureAwait(false))
        {
            return new CheckpointReport(
                CheckpointState.HistoryRewritten,
                checkpoint,
                head,
                [],
                working,
                $"チェックポイント {Short(checkpoint)} が現在の履歴に含まれていません（履歴が書き換えられた）。チェックポイントからの差分として取り込み直してください");
        }

        var allowed = AllowedSignersFile.Load(_allowedSignersPath);
        var commits = await git.LogAsync($"{checkpoint}..{head}", _allowedSignersPath, cancellationToken).ConfigureAwait(false);
        var unknown = new List<CommitFinding>();
        foreach (var commit in commits)
        {
            var trust = SignaturePolicy.Classify(commit, allowed);
            if (SignaturePolicy.IsTrusted(trust))
            {
                continue;
            }

            if (commit.IsMerge && _merges.Contains(commit.Hash))
            {
                continue;
            }

            unknown.Add(new CommitFinding(commit.Hash, commit.Subject, SignaturePolicy.Describe(trust)));
        }

        if (unknown.Count > 0)
        {
            return new CheckpointReport(
                CheckpointState.UnsignedCommits,
                checkpoint,
                head,
                unknown,
                working,
                string.Create(CultureInfo.InvariantCulture, $"チェックポイント以降に、把握していないコミットが {unknown.Count} 件あります（直接コミットされた）。custos commit で分類し直してください"));
        }

        return new CheckpointReport(CheckpointState.AdvancedKnown, checkpoint, head, [], working, "チェックポイント以降のコミットは、すべて把握済みです" + WorkingNote(working));
    }

    /// <summary>問題がなければ、チェックポイントを現在の位置に進める。初めてなら現在の位置で作る。</summary>
    public async Task<CheckpointReport> AdvanceAsync(GitClient git, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        var report = await CheckAsync(git, cancellationToken).ConfigureAwait(false);
        if (!report.Ok)
        {
            return report;
        }

        if (report.Head != null)
        {
            _store.Save(git.RepoPath, report.Head);
        }

        return report;
    }

    /// <summary>
    /// 直接コミットされた場合の復帰：チェックポイントまで履歴を戻し（soft reset）、変更内容は作業中の状態に残す。
    /// その後、custos commit で分類し直してコミットさせる。
    /// </summary>
    public async Task<RecoveryResult> RecoverDirectCommitsAsync(GitClient git, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        var report = await CheckAsync(git, cancellationToken).ConfigureAwait(false);
        if (report.State != CheckpointState.UnsignedCommits || report.Checkpoint == null)
        {
            return new RecoveryResult(false, report.Checkpoint, [], "直接コミットされた状態ではありません：" + report.Message);
        }

        var undone = (await git.LogAsync($"{report.Checkpoint}..HEAD", _allowedSignersPath, cancellationToken).ConfigureAwait(false)).Select(c => c.Hash).ToList();
        await git.OutputAsync(["reset", "--soft", report.Checkpoint], cancellationToken).ConfigureAwait(false);
        return new RecoveryResult(
            true,
            report.Checkpoint,
            undone,
            string.Create(CultureInfo.InvariantCulture, $"チェックポイント {Short(report.Checkpoint)} まで戻しました。{undone.Count} 件のコミットの変更は作業中の状態に残っています。custos commit で分類し直してください"));
    }

    /// <summary>
    /// 履歴が書き換えられた場合の復帰：チェックポイントの位置を示し、そこからの差分として取り込み直させる。
    /// チェックポイントのコミットがまだあれば、HEAD をそこに移し（soft reset）、現在の内容を作業中の変更として残す。
    /// </summary>
    public async Task<RecoveryResult> PrepareReimportAsync(GitClient git, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        var report = await CheckAsync(git, cancellationToken).ConfigureAwait(false);
        if (report.State != CheckpointState.HistoryRewritten || report.Checkpoint == null)
        {
            return new RecoveryResult(false, report.Checkpoint, [], "履歴が書き換えられた状態ではありません：" + report.Message);
        }

        if (!await git.CommitExistsAsync(report.Checkpoint, cancellationToken).ConfigureAwait(false))
        {
            return new RecoveryResult(false, report.Checkpoint, [], $"チェックポイント {report.Checkpoint} のコミットがリポジトリにありません。チェックポイントの内容を取り寄せてから、差分として取り込み直してください");
        }

        await git.OutputAsync(["reset", "--soft", report.Checkpoint], cancellationToken).ConfigureAwait(false);
        return new RecoveryResult(
            true,
            report.Checkpoint,
            [],
            $"HEAD をチェックポイント {Short(report.Checkpoint)} に戻しました。現在の内容はチェックポイントからの差分として作業中の状態にあります。custos commit で取り込み直してください");
    }

    private static string WorkingNote(bool working) => working ? "（作業中の変更あり：これからコミットする内容として扱う）" : string.Empty;

    private static string Short(string hash) => hash.Length > 10 ? hash[..10] : hash;
}
