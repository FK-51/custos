namespace Custos.Core.Config;

// 設定ファイルの型（設計書 第18章）。書式の検査は ConfigParsers が行い、ここでは読み終えた値だけを持つ。

/// <summary>プラットフォーム。</summary>
public enum Platform
{
    /// <summary>Webアプリ。</summary>
    Web,

    /// <summary>Unity。</summary>
    Unity,

    /// <summary>Unreal Engine。</summary>
    Unreal,

    /// <summary>独自エンジン。</summary>
    Custom,
}

/// <summary>重要度。</summary>
public enum Criticality
{
    /// <summary>低い。</summary>
    Low,

    /// <summary>中くらい。</summary>
    Medium,

    /// <summary>高い（個人情報や決済を扱う）。</summary>
    High,
}

/// <summary>テスト用APIの有無（設計書 第14.6節、第24.6節）。</summary>
public enum TestApiMode
{
    /// <summary>テスト用APIなし。能力は app.yaml で宣言する。</summary>
    None,

    /// <summary>HTTP の /__verify/*。能力は API の応答から読む。</summary>
    Http,
}

/// <summary>成熟度のレベル（設計書 第17.5節）。Level 3 は a/b/c の区分を持つ。</summary>
public sealed record MaturityLevel(int Level, char? Sub)
{
    /// <summary>レベル0。</summary>
    public static readonly MaturityLevel Zero = new(0, null);

    /// <summary>文字列から作る（"2"、"3"、"3a"）。</summary>
    public static bool TryParse(string text, out MaturityLevel level)
    {
        ArgumentNullException.ThrowIfNull(text);
        level = Zero;
        text = text.Trim();
        if (text.Length == 1 && text[0] is >= '0' and <= '3')
        {
            level = new MaturityLevel(text[0] - '0', null);
            return true;
        }

        if (text.Length == 2 && text[0] == '3' && text[1] is 'a' or 'b' or 'c')
        {
            level = new MaturityLevel(3, text[1]);
            return true;
        }

        return false;
    }

    /// <summary>比べるための順位（0, 1, 2, 3a, 3b, 3c の順）。区分のない 3 は 3a と同じ。</summary>
    public int Rank => Level < 3 ? Level : 3 + (Sub switch { 'b' => 1, 'c' => 2, _ => 0 });

    /// <summary>この要求を満たすか。</summary>
    public bool Satisfies(MaturityLevel required)
    {
        ArgumentNullException.ThrowIfNull(required);
        return Rank >= required.Rank;
    }

    /// <inheritdoc />
    public override string ToString() => Sub is { } s ? $"{Level}{s}" : Level.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>app.yaml。</summary>
public sealed record AppConfig(
    string Name,
    Platform Platform,
    Criticality Criticality,
    MaturityLevel RequiredMaturity,
    TestApiMode TestApi,
    int TestApiPort,
    string Adapter,
    IReadOnlyDictionary<string, string> DeclaredCapabilities,
    string? Description);

/// <summary>状態の値の型。</summary>
public enum StateType
{
    /// <summary>整数。</summary>
    Integral,

    /// <summary>小数。</summary>
    Real,

    /// <summary>真偽値。</summary>
    Bool,

    /// <summary>文字列。</summary>
    Text,

    /// <summary>識別子（文字列か整数）。</summary>
    Id,

    /// <summary>列。</summary>
    List,

    /// <summary>オブジェクト。</summary>
    Map,

    /// <summary>3次元の座標。</summary>
    Vec3,

    /// <summary>日時。</summary>
    Timestamp,

    /// <summary>任意の JSON。</summary>
    Json,
}

/// <summary>状態の読み方。</summary>
public abstract record StateSource
{
    /// <summary>読み方の種類（sql / socket / dom / adapter）。</summary>
    public abstract string Kind { get; }
}

/// <summary>SQL で読む（対象ごとの ID と値を返す SQL）。</summary>
public sealed record SqlSource(string Sql) : StateSource
{
    /// <inheritdoc />
    public override string Kind => "sql";
}

/// <summary>Socket.IO で送られた状態から読む（受け取った利用者ごとに、最後に届いた値）。</summary>
public sealed record SocketSource(IReadOnlyList<string> Events, string Path) : StateSource
{
    /// <inheritdoc />
    public override string Kind => "socket";
}

/// <summary>画面（DOM）から読む。</summary>
public sealed record DomSource(string Selector, string Read, string? Page) : StateSource
{
    /// <inheritdoc />
    public override string Kind => "dom";
}

/// <summary>アダプタの state.get で読む（ゲーム）。</summary>
public sealed record AdapterSource(string Key) : StateSource
{
    /// <inheritdoc />
    public override string Kind => "adapter";
}

/// <summary>状態スキーマの1つのキー。</summary>
public sealed record StateKey(string Name, StateType Type, string Per, StateSource Source, string? Description);

/// <summary>決定性がない場合の比べ方（設計書 第18.2節 determinism、第22章）。</summary>
public sealed record DeterminismConfig(
    IReadOnlyList<string> Volatile,
    string IdNormalization,
    IReadOnlyList<string> Sort,
    int HonestRuns,
    string UndeclaredNoise,
    int EnvelopeRuns)
{
    /// <summary>宣言がない場合の値（最も厳しい側：除外も正規化もしない、宣言のないぶれは報告する）。</summary>
    public static readonly DeterminismConfig Strict = new([], "none", [], 3, "report", 39);
}

/// <summary>state.yaml。</summary>
public sealed record StateConfig(IReadOnlyDictionary<string, StateKey> Keys, DeterminismConfig Determinism);

/// <summary>権威。</summary>
public enum Authority
{
    /// <summary>サーバー権威。</summary>
    Server,

    /// <summary>クライアント権威。</summary>
    Client,

    /// <summary>ホスト権威（P2P）。</summary>
    Host,
}

/// <summary>検証の種類。</summary>
public enum ValidationKind
{
    /// <summary>なし。</summary>
    None,

    /// <summary>妥当性チェック（速度上限など）。</summary>
    Plausibility,

    /// <summary>サーバーで再計算。</summary>
    Full,
}

/// <summary>影響範囲。後ろほど厳しい。</summary>
public enum Scope
{
    /// <summary>自分だけ。</summary>
    Self,

    /// <summary>他プレイヤーに見える。</summary>
    VisibleToOthers,

    /// <summary>永続化される。</summary>
    Persisted,

    /// <summary>経済に影響する。</summary>
    Economy,
}

/// <summary>可視範囲（設計書 第25.5節）。</summary>
public enum Visibility
{
    /// <summary>誰に送ってもよい。</summary>
    All,

    /// <summary>同じ部屋（対局）の参加者にだけ送ってよい。</summary>
    Members,

    /// <summary>本人にだけ送ってよい。</summary>
    Owner,

    /// <summary>誰にも送ってはいけない。</summary>
    None,
}

/// <summary>検証の宣言。</summary>
public sealed record ValidationSpec(ValidationKind Kind, IReadOnlyDictionary<string, double> Parameters);

/// <summary>権威マップの1項目。Declared が false なら defaults から補ったもの。</summary>
public sealed record AuthorityEntry(string Key, Authority Authority, ValidationSpec Validation, Scope Scope, Visibility? Visibility, bool Declared);

/// <summary>データの所有者と、読み書きできる利用者の種類。</summary>
public sealed record OwnershipEntry(string Entity, string OwnerKey, IReadOnlyList<string> Read, IReadOnlyList<string> Write);

/// <summary>受け取ってよい入力（イベント名ごと、またはエンドポイントごと）。</summary>
public sealed record InputEntry(string Name, string Kind, string Target, IReadOnlyList<string> Accepts);

/// <summary>局面ごとに、誰がどのイベントを送ってよいか（設計書 第25.4節）。</summary>
public sealed record PhaseEntry(string Name, IReadOnlyDictionary<string, IReadOnlyList<string>> Allowed);

/// <summary>サーバーから送ってよい項目の一覧（送信イベントごと）。一覧にない項目は「未知の項目」として報告する。</summary>
public sealed record SentSchema(string Event, IReadOnlyList<string> Paths);

/// <summary>authority.yaml。</summary>
public sealed record AuthorityConfig(
    IReadOnlyList<string> Actors,
    AuthorityEntry Defaults,
    IReadOnlyDictionary<string, AuthorityEntry> State,
    IReadOnlyDictionary<string, OwnershipEntry> Ownership,
    IReadOnlyDictionary<string, InputEntry> Inputs,
    IReadOnlyDictionary<string, PhaseEntry> Phases,
    IReadOnlyDictionary<string, SentSchema> Sent)
{
    /// <summary>キーの権威を引く。宣言がなければ defaults（最も厳しい扱い）を返す。</summary>
    public AuthorityEntry Resolve(string key)
    {
        if (State.TryGetValue(key, out var entry))
        {
            return entry;
        }

        return Defaults with { Key = key, Declared = false };
    }
}

/// <summary>重大度（設計書 第26.2節）。</summary>
public enum Severity
{
    /// <summary>致命的。機械判定でゼロを目指す。</summary>
    Critical,

    /// <summary>高。</summary>
    High,

    /// <summary>中。</summary>
    Medium,

    /// <summary>低。</summary>
    Low,
}

/// <summary>不変条件の書き方。</summary>
public enum InvariantKind
{
    /// <summary>アプリのDBに対する SQL（違反している行を返す）。</summary>
    Sql,

    /// <summary>送信された状態（通信の記録）に対する SQL。</summary>
    SentSql,

    /// <summary>アプリのDB（app）と通信の記録（rec）の両方を使う SQL。写しの突き合わせ。</summary>
    CrossSql,

    /// <summary>1秒あたりの変化量の上限（ゲーム）。</summary>
    Delta,

    /// <summary>フレームワークに組み込みの判定。</summary>
    Builtin,
}

/// <summary>不変条件1件。</summary>
public sealed record InvariantSpec(
    string Id,
    Severity Severity,
    int Stage,
    string? Category,
    InvariantKind Kind,
    string? Query,
    string? DeltaKey,
    double? MaxPerSecond,
    IReadOnlyList<string> Exceptions,
    string? Builtin,
    IReadOnlyDictionary<string, string> Parameters,
    string? Description);

/// <summary>invariants.yaml。</summary>
public sealed record InvariantsConfig(IReadOnlyList<InvariantSpec> Invariants);

/// <summary>個人情報のフラグ。</summary>
public sealed record PiiFlags(string Table, IReadOnlyList<string> Columns, int Count);

/// <summary>security.yaml。</summary>
public sealed record SecurityConfig(
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> NotApplicable,
    PiiFlags? Pii,
    string? SecretFile,
    IReadOnlyList<string> SecretStateKeys,
    bool XssMarkers,
    bool SqlMarkers);

/// <summary>ノードの資源。</summary>
public sealed record NodeResources(double? Cpus, string? Cpuset, long? MemoryBytes, long? DiskIops);

/// <summary>仮想ネットワークの条件。</summary>
public sealed record NetworkConditions(long? BandwidthBps, TimeSpan? Latency, TimeSpan? Jitter, double? Loss);

/// <summary>計測を無効とする基準（設計書 第23.1節）。</summary>
public sealed record ValidityCriteria(
    double LoadCpuMax,
    double SendLagP99Max,
    double ReferenceDriftMax,
    bool SwapAllowed,
    double CpuStealMax,
    bool SteadyStateCheck,
    int Runs,
    double DetectRegression,
    double MaxCv)
{
    /// <summary>第23.1節の初期値。</summary>
    public static readonly ValidityCriteria Default = new(0.70, 0.02, 0.03, false, 0.05, true, 5, 0.10, 0.05);
}

/// <summary>リソースプロファイル。</summary>
public sealed record ResourceProfile(
    string Name,
    string Target,
    IReadOnlyDictionary<string, NodeResources> Nodes,
    IReadOnlyDictionary<string, NodeResources> Machines,
    NetworkConditions? Network,
    ValidityCriteria Validity);

/// <summary>対象アプリのコンテナの定義（本番と同じ設定にする。検証用の値を足さない）。</summary>
public sealed record AppNodeConfig(
    string BuildContext,
    string Dockerfile,
    IReadOnlyDictionary<string, string> ImagePins,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Command,
    int Port,
    int ServerPort,
    string DataDir,
    string DbFile,
    string HealthPath,
    string SocketIoPath,
    string? User,
    string? TestBuildDockerfile,
    string? TestApiEnable);

/// <summary>ノード構成（設計書 第13.2節）。</summary>
public sealed record NodeLayout(int HonestClients, int AttackerClients, int Browsers, bool Sandbox);

/// <summary>environment.yaml。</summary>
public sealed record EnvironmentConfig(
    string Project,
    string PublicHost,
    int PublicPort,
    AppNodeConfig App,
    NodeLayout Nodes,
    IReadOnlyDictionary<string, string> BaseImages,
    IReadOnlyList<string> EgressAllow,
    IReadOnlyDictionary<string, ResourceProfile> ResourceProfiles);

/// <summary>SLO 1件。</summary>
public sealed record SloSpec(string Id, string Profile, string Scenario, int ConcurrentUsers, string Metric, double Threshold, string Unit);

/// <summary>換算係数1件（設計書 第18.7節）。</summary>
public sealed record CalibrationFactorSpec(
    string From,
    string To,
    string Scenario,
    string Metric,
    double Factor,
    double FactorLow,
    double FactorHigh,
    string Bottleneck,
    DateOnly MeasuredAt,
    DateOnly ValidUntil,
    double MaxPredictionError,
    IReadOnlyList<string> InvalidatedBy);

/// <summary>performance.yaml。</summary>
public sealed record PerformanceConfig(IReadOnlyList<SloSpec> Slo, IReadOnlyList<CalibrationFactorSpec> Calibration);

/// <summary>誤り率の上限の扱い。</summary>
public enum ErrorUpperMode
{
    /// <summary>r から計算する（第21.8節）。</summary>
    Auto,

    /// <summary>値を直接指定する。</summary>
    Fixed,

    /// <summary>使わない（プログラム判定でゼロを目指す）。</summary>
    None,
}

/// <summary>抜き取り検証の種類ごとの設定。</summary>
public sealed record SamplingCategory(string Name, double? LossRatio, ErrorUpperMode Mode, double? ErrorUpper, double? FallbackErrorUpper);

/// <summary>sampling.yaml。</summary>
public sealed record SamplingConfig(
    double Confidence,
    string Unit,
    IReadOnlyDictionary<string, SamplingCategory> Categories,
    string VerifierRole,
    bool Blind,
    double MinVerifierSensitivity,
    int HumanAuditPerRelease,
    double FreeExplorationShare);

/// <summary>役割ごとのエージェントとモデル。</summary>
public sealed record RoleAssignment(string Role, string Agent, string? Model);

/// <summary>agents.yaml。</summary>
public sealed record AgentsConfig(
    string DefaultAgent,
    string? DefaultModel,
    IReadOnlyDictionary<string, RoleAssignment> Roles,
    IReadOnlyList<IReadOnlyList<string>> Differ,
    string OnViolation,
    double? MaxBudgetUsdPerRun);
