namespace Custos.Cli.Commands;

/// <summary>コマンドの処理。</summary>
public delegate Task<int> CommandHandler(CliContext context, CliArgs args, CancellationToken cancellationToken);

/// <summary>コマンド1つ。</summary>
public sealed record CommandSpec(string Name, string Usage, string Description, CommandHandler Handler);

/// <summary>コマンドの一覧（指示書 付録A と、段階1以降のコマンド）。</summary>
public static class CommandTable
{
    /// <summary>一覧を作る。</summary>
    public static IReadOnlyList<CommandSpec> Build() =>
    [
        // 段階0：土台
        new("config check", "config check", "設定ファイルの書式と参照を検査する（A-2、A-4）", LabCommands.ConfigCheckAsync),
        new("up", "up [--build production|test] [--profile <名前>] [--break <壊し方>]... [--egress]", "検証環境を起動する", LabCommands.UpAsync),
        new("down", "down", "検証環境を片付ける", LabCommands.DownAsync),
        new("status", "status", "検証環境の状態を表示する", LabCommands.StatusAsync),
        new("snapshot save", "snapshot save <名前> [--overwrite]", "スナップショットを取る", LabCommands.SnapshotSaveAsync),
        new("snapshot list", "snapshot list", "スナップショットの一覧", LabCommands.SnapshotListAsync),
        new("reset", "reset <名前>", "スナップショットの状態に戻す", LabCommands.ResetAsync),
        new("settle", "settle [--quiet-ms 500] [--timeout-ms 15000]", "通信と DB の更新が止まるまで待つ", LabCommands.SettleAsync),
        new("state get", "state get <キー>", "状態を読む", LabCommands.StateGetAsync),
        new("state dump", "state dump", "すべての状態を読む", LabCommands.StateDumpAsync),
        new("record show", "record show [--since <seq>] [--event <名前>] [--all] [--raw]", "通信の記録をイベント単位で表示する", LabCommands.RecordShowAsync),
        new("logs", "logs", "アプリのコンテナの標準出力と標準エラー出力", LabCommands.LogsAsync),
        new("call", "call <client|attacker|browser|ops> <method> [<JSON>]", "コンテナ内の部品を直接呼ぶ（調べもの用）", LabCommands.CallAsync),
        new("scenario run", "scenario run <ファイル>", "シナリオを実行する", ScenarioCommands.RunAsync),
        new("conformance", "conformance [--item A-1,B-2] [--break <壊し方>] [--all-breakage]", "適合テストを実行する", ConformanceCommands.RunAsync),
        new("bug add", "bug add --title <説明> [--found-by setup] [--category ...] [--severity ...]", "不具合を1件登録する（追記だけ）", RegistryCommands.BugAddAsync),
        new("bug update", "bug update --id <ID,...> [--severity ...] [--category ...] [--duplicate-of <ID>] --reason <理由>", "登録簿の値を直す（履歴が残る）", RegistryCommands.BugUpdateAsync),
        new("bug export", "bug export", "登録簿を検証用リポジトリに書き出す", RegistryCommands.BugExportAsync),
        new("commit", "commit --type <feat|fix|refactor|test|chore> [--bug <ID>] -m <説明> [--repo <dir>] [--sign]", "種類を付けてコミットする", RegistryCommands.CommitAsync),
        new("results list", "results list", "結果の一覧", LabCommands.ResultsListAsync),
        new("results verify", "results verify", "結果の改ざんを検査する（連鎖とハッシュ）", LabCommands.ResultsVerifyAsync),
        new("results close", "results close --id <ID,...> --reason <理由>", "running のまま残った実行（前のプロセスが封をせずに終わったもの）に、理由を書いて封をする", LabCommands.ResultsCloseAsync),

        // 段階1：機械判定
        new("judge", "judge [--scenario <ファイル>]", "汎用の判定と不変条件で、いまの状態を判定する", JudgeCommands.JudgeAsync),
        new("fuzz", "fuzz [--event <名前>]... [--rounds N] [--seed N]", "イベントのファジング", JudgeCommands.FuzzAsync),
        new("probe phases", "probe phases", "状態遷移と選択肢の検査（局面 × イベント × 送り手）", JudgeCommands.PhasesAsync),
        new("probe authority", "probe authority", "宣言と実態のずれの検査（サーバー権威の値を偽の値で送る）", JudgeCommands.AuthorityAsync),
        new("probe secrets", "probe secrets [--games N]", "隠し情報と再接続トークンの検査", JudgeCommands.SecretsAsync),
        new("bot play", "bot play [--games N] [--seed N]", "相手役のボットで対局を最後まで回す", JudgeCommands.BotPlayAsync),
        new("stage1", "stage1", "段階1の機械判定をまとめて実行する", JudgeCommands.Stage1Async),

        // 段階2：再現性
        new("replay", "replay [--game <ID>]", "手のログからの再生（一致の検査）", ReproCommands.ReplayAsync),
        new("determinism", "determinism [--runs 5] [--scenario <ファイル>]", "決定性の等級を測る（再実行の一致、正直なクライアント同士の比較）", ReproCommands.DeterminismAsync),
        new("probe time", "probe time", "制限時間の検査（時間切れと着手を同時に）", ReproCommands.TimeAsync),
        new("probe disruption", "probe disruption", "切断・再接続・再起動の注入", ReproCommands.DisruptionAsync),

        // 段階3：AIによる探索と修正
        new("ledger build", "ledger build", "確認対象の台帳を作る", AgentCommands.LedgerBuildAsync),
        new("explore", "explore [--role explorer|attacker] [--targets N] [--agent scripted|claude-code]", "AIによる探索（台帳から割り当てる）", AgentCommands.ExploreAsync),
        new("loop", "loop [--iterations N] [--agent scripted|claude-code]", "探索・判定・修正のループを回す", AgentCommands.LoopAsync),
        new("hidden run", "hidden run", "隠しシナリオを実行する（リリース判定のときだけ）", AgentCommands.HiddenAsync),
        new("keys init", "keys init [--owner <名前>]", "フレームワークの署名鍵を作る", RegistryCommands.KeysInitAsync),
        new("intake check", "intake check --repo <dir> --base <ref> --head <ref>", "取り込み口の検査", RegistryCommands.IntakeCheckAsync),

        // 段階4：校正と統計
        new("seeds check", "seeds check", "校正用バグの品質チェック（効いているか、自明すぎないか、層の割り当て）", CalibrationCommands.SeedsCheckAsync),
        new("calibrate", "calibrate [--seeds <ID,...>] [--agent scripted|claude-code]", "校正を実行する（検出率の測定）", CalibrationCommands.CalibrateAsync),
        new("canary", "canary [--count 3]", "毎ループのカナリア", CalibrationCommands.CanaryAsync),
        new("sample", "sample [--n N]", "抜き取り検証（ブラインド判定と誤り率の上限）", CalibrationCommands.SampleAsync),
        new("maturity", "maturity", "成熟度のレベルと個別の数値", CalibrationCommands.MaturityAsync),
        new("release", "release", "リリース判定", CalibrationCommands.ReleaseAsync),

        // 段階6：広げる
        new("template init", "template init <dir> [--template web-socketio]", "ひな形から検証用リポジトリを作る", TemplateCommands.InitAsync),
        new("selftest", "selftest [--data-dir-selftest <dir>] [--keep]", "参照アプリ（既知のバグ入り）で、フレームワーク自体の回帰テストをする", SelftestCommands.RunAsync),
        new("reference check", "reference check [--game <ID>]", "参照モデルとの食い違いを調べる（手のログを参照モデルで再生する）", ReferenceCommands.CheckAsync),

        // 段階5：性能
        new("perf run", "perf run [--profile mini-1] [--games N] [--concurrency N] [--runs N]", "負荷をかけて計測する", PerfCommands.RunAsync),
        new("perf compare", "perf compare <基準の結果> <比べる結果>", "ビルド間の比較", PerfCommands.CompareAsync),
        new("perf capacity", "perf capacity [--profile mini-1]", "基準を守れる最大の同時対局数（容量）", PerfCommands.CapacityAsync),
    ];

    /// <summary>位置引数の先頭から、いちばん長く一致するコマンドを探す。</summary>
    public static (CommandSpec? Command, int Consumed) Find(IReadOnlyList<CommandSpec> commands, IReadOnlyList<string> positional)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(positional);
        for (var length = Math.Min(3, positional.Count); length >= 1; length--)
        {
            var name = string.Join(' ', positional.Take(length));
            var command = commands.FirstOrDefault(c => c.Name == name);
            if (command != null)
            {
                return (command, length);
            }
        }

        return (null, 0);
    }

    /// <summary>使い方を表示する。</summary>
    public static void PrintHelp(IReadOnlyList<CommandSpec> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        Console.Out.WriteLine("custos — AIエージェント開発のための自律検証環境（Quis custodiet ipsos custodes?）");
        Console.Out.WriteLine();
        Console.Out.WriteLine("共通の指定：--verify-repo <dir> --data-dir <dir> --app-repo <dir>（環境変数 CUSTOS_VERIFY_REPO、CUSTOS_DATA_DIR、CUSTOS_APP_REPO でも可）、--json");
        Console.Out.WriteLine();
        foreach (var command in commands)
        {
            Console.Out.WriteLine($"  custos {command.Usage}");
            Console.Out.WriteLine($"      {command.Description}");
        }
    }
}
