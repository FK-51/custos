using System.Collections.Generic;

namespace Custos.Protocol;

/// <summary>
/// ゲームエンジン向けのプロトコル（設計書 第14.7節）の定数。JSON-RPC 2.0 を WebSocket で送る。
/// 型は Unity 側の SDK と共有するため、netstandard2.1 で書き、直列化の方式には依存しない（名前は snake_case の JSON に対応する）。
/// </summary>
public static class ProtocolInfo
{
    /// <summary>プロトコル全体の版。能力ごとの版とは別に付ける（第14.8節）。</summary>
    public const string Version = "0.1";

    /// <summary>JSON-RPC の版。</summary>
    public const string JsonRpc = "2.0";
}

/// <summary>メソッドの名前（第14.7節の分類）。</summary>
public static class Methods
{
    /// <summary>握手（接続直後）。</summary>
    public const string Initialize = "initialize";

    /// <summary>観測：状態を読む。</summary>
    public const string StateGet = "state.get";

    /// <summary>観測：状態の変化を購読する。</summary>
    public const string StateSubscribe = "state.subscribe";

    /// <summary>観測：状態のハッシュ（決定性の定義に使う。第14.8節）。</summary>
    public const string StateHash = "state.hash";

    /// <summary>観測：スクリーンショット（大きなデータは共有フォルダに保存してパスを返す）。</summary>
    public const string ScreenshotCapture = "screenshot.capture";

    /// <summary>操作：入力を送る（キー、スティック、タッチ）。</summary>
    public const string InputSend = "input.send";

    /// <summary>操作：デバッグコンソールのコマンド。</summary>
    public const string CommandExec = "command.exec";

    /// <summary>制御：スナップショットを取る。</summary>
    public const string SnapshotSave = "snapshot.save";

    /// <summary>制御：リセットする。</summary>
    public const string Reset = "reset";

    /// <summary>制御：乱数のシードを固定する。</summary>
    public const string SeedSet = "seed.set";

    /// <summary>時間：止める。</summary>
    public const string TimePause = "time.pause";

    /// <summary>時間：指定したティック数だけ進める。</summary>
    public const string TimeStep = "time.step";

    /// <summary>時間：速さを変える。</summary>
    public const string TimeScale = "time.scale";

    /// <summary>記録と再生：記録を始める。</summary>
    public const string RecordStart = "record.start";

    /// <summary>記録と再生：記録を止める。</summary>
    public const string RecordStop = "record.stop";

    /// <summary>記録と再生：再生を読み込む。</summary>
    public const string ReplayLoad = "replay.load";

    /// <summary>注入：異常の一覧。</summary>
    public const string FaultList = "fault.list";

    /// <summary>注入：異常を有効にする。</summary>
    public const string FaultEnable = "fault.enable";

    /// <summary>通知（アダプタから）：ログ。</summary>
    public const string NotifyLog = "notify.log";

    /// <summary>通知（アダプタから）：状態の変化。</summary>
    public const string NotifyState = "notify.state";

    /// <summary>通知（アダプタから）：例外。</summary>
    public const string NotifyException = "notify.exception";
}

/// <summary>能力の名前（第14.3節の能力宣言）。値は能力ごとの版。</summary>
public static class Capabilities
{
    /// <summary>状態の読み取り。</summary>
    public const string StateRead = "state.read";

    /// <summary>リセット。</summary>
    public const string Reset = "reset";

    /// <summary>シードの固定。</summary>
    public const string Seed = "seed";

    /// <summary>ティック単位の時間の制御。</summary>
    public const string TimeStep = "time.step";

    /// <summary>記録と再生。</summary>
    public const string Replay = "replay";

    /// <summary>異常の注入。</summary>
    public const string Fault = "fault";
}

/// <summary>アダプタの役割。</summary>
public static class Roles
{
    /// <summary>サーバー側。</summary>
    public const string Server = "server";

    /// <summary>クライアント側。</summary>
    public const string Client = "client";
}

/// <summary>JSON-RPC の要求。</summary>
/// <typeparam name="TParams">引数の型。</typeparam>
public sealed class RpcRequest<TParams>
{
    /// <summary>"2.0"。</summary>
    public string Jsonrpc { get; set; } = ProtocolInfo.JsonRpc;

    /// <summary>要求の ID（通知では null）。</summary>
    public long? Id { get; set; }

    /// <summary>メソッドの名前。</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>引数。</summary>
    public TParams? Params { get; set; }
}

/// <summary>JSON-RPC の応答。</summary>
/// <typeparam name="TResult">結果の型。</typeparam>
public sealed class RpcResponse<TResult>
{
    /// <summary>"2.0"。</summary>
    public string Jsonrpc { get; set; } = ProtocolInfo.JsonRpc;

    /// <summary>要求の ID。</summary>
    public long? Id { get; set; }

    /// <summary>結果（失敗なら null）。</summary>
    public TResult? Result { get; set; }

    /// <summary>失敗（成功なら null）。</summary>
    public RpcError? Error { get; set; }
}

/// <summary>JSON-RPC の失敗。</summary>
public sealed class RpcError
{
    /// <summary>メソッドがない。</summary>
    public const int MethodNotFound = -32601;

    /// <summary>引数が正しくない。</summary>
    public const int InvalidParams = -32602;

    /// <summary>能力がない（このプロトコルで定める）。</summary>
    public const int CapabilityMissing = -32001;

    /// <summary>番号。</summary>
    public int Code { get; set; }

    /// <summary>説明。</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>状態スキーマの1キー（state.yaml と同じ名前と型。第14.8節「キーの名前の統一」）。</summary>
public sealed class StateKeySchema
{
    /// <summary>型（int、float、bool、string、id、list、map、vec3、timestamp、json）。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>対象（entity、player、game など）。</summary>
    public string Per { get; set; } = string.Empty;
}

/// <summary>握手の要求（フレームワークから）。</summary>
public sealed class InitializeParams
{
    /// <summary>フレームワークが話すプロトコルの版。</summary>
    public string ProtocolVersion { get; set; } = ProtocolInfo.Version;

    /// <summary>フレームワークの名前と版。</summary>
    public string Client { get; set; } = string.Empty;

    /// <summary>大きなデータ（スクリーンショット、動画）を置く共有フォルダ。</summary>
    public string? SharedDirectory { get; set; }
}

/// <summary>握手の応答（アダプタから。第14.7節の例）。</summary>
public sealed class InitializeResult
{
    /// <summary>アダプタが話すプロトコルの版。</summary>
    public string ProtocolVersion { get; set; } = ProtocolInfo.Version;

    /// <summary>server か client。</summary>
    public string Role { get; set; } = Roles.Server;

    /// <summary>能力と、その版。</summary>
    public Dictionary<string, string> Capabilities { get; set; } = new Dictionary<string, string>();

    /// <summary>状態スキーマ。</summary>
    public Dictionary<string, StateKeySchema> StateSchema { get; set; } = new Dictionary<string, StateKeySchema>();
}

/// <summary>state.get の引数。</summary>
public sealed class StateGetParams
{
    /// <summary>読むキー（空ならすべて）。</summary>
    public List<string> Keys { get; set; } = new List<string>();
}

/// <summary>
/// 状態の観測。読むたびにティック番号と時刻を返す（第14.8節「観測には時点を付ける」）。
/// 値は直列化の方式に依存しないよう、JSON の文字列で持つ。
/// </summary>
public sealed class StateObservation
{
    /// <summary>ティック番号。</summary>
    public long Tick { get; set; }

    /// <summary>時刻（Unix ミリ秒）。</summary>
    public long TimeMs { get; set; }

    /// <summary>キーごと、対象ごとの値（JSON の文字列）。</summary>
    public Dictionary<string, Dictionary<string, string>> Values { get; set; } = new Dictionary<string, Dictionary<string, string>>();
}

/// <summary>state.hash の結果。</summary>
public sealed class StateHashResult
{
    /// <summary>ティック番号。</summary>
    public long Tick { get; set; }

    /// <summary>ハッシュ（16進）。</summary>
    public string Hash { get; set; } = string.Empty;
}

/// <summary>input.send の引数。</summary>
public sealed class InputParams
{
    /// <summary>種類（key、stick、touch、button）。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>対象（キーの名前、スティックの番号など）。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>値（押す・離す、座標など。JSON の文字列）。</summary>
    public string Value { get; set; } = "null";

    /// <summary>何ティックの間続けるか。</summary>
    public int DurationTicks { get; set; }
}

/// <summary>time.step の引数。</summary>
public sealed class TimeStepParams
{
    /// <summary>進めるティック数。</summary>
    public int Ticks { get; set; } = 1;
}

/// <summary>seed.set の引数。</summary>
public sealed class SeedParams
{
    /// <summary>シード（null で本物の乱数に戻す）。</summary>
    public long? Seed { get; set; }
}

/// <summary>screenshot.capture の結果（大きなデータは共有フォルダのパスで返す）。</summary>
public sealed class ScreenshotResult
{
    /// <summary>ティック番号。</summary>
    public long Tick { get; set; }

    /// <summary>共有フォルダの中のパス。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>幅。</summary>
    public int Width { get; set; }

    /// <summary>高さ。</summary>
    public int Height { get; set; }
}

/// <summary>異常の1件（fault.list の結果）。</summary>
public sealed class FaultInfo
{
    /// <summary>名前。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>説明。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>有効か。</summary>
    public bool Enabled { get; set; }
}

/// <summary>通知（アダプタからフレームワークへ）：例外。</summary>
public sealed class ExceptionNotification
{
    /// <summary>ティック番号。</summary>
    public long Tick { get; set; }

    /// <summary>例外の型。</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>説明。</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>スタックトレース。</summary>
    public string? Stack { get; set; }
}
