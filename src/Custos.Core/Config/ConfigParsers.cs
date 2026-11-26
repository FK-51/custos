using System.Globalization;
using System.Text.RegularExpressions;

namespace Custos.Core.Config;

/// <summary>
/// 設定ファイルごとの書式の検査と読み込み（設計書 第18章）。
/// - すべてのファイルに version を必須にする。
/// - 書式にないキーは誤りにする。
/// - 書き忘れは厳しい側に倒す（宣言のない項目は最も厳しい条件で扱う）。
/// </summary>
public static partial class ConfigParsers
{
    /// <summary>対応している書式の版。</summary>
    public const int SupportedVersion = 1;

    private static readonly string[] SourceKinds = ["sql", "socket", "dom", "adapter"];
    private static readonly string[] InvariantKinds = ["sql", "sent_sql", "cross_sql", "delta", "builtin"];

    /// <summary>攻撃目標の名前（設計書 第3.1節、第25.7節の読み替え）。</summary>
    public static readonly IReadOnlyList<string> KnownGoals =
    [
        "pii_theft", "secret_theft", "server_intrusion", "unauthenticated_access", "tampering",
        "hidden_info", "seat_hijack", "rule_break", "server_down", "xss", "game_stall", "sqli",
    ];

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+)+$")]
    private static partial Regex StateKeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9_-]*$")]
    private static partial Regex NamePattern();

    private static bool CheckVersion(YMap map)
    {
        var version = map.Whole("version", required: true);
        if (version is { } v && v != SupportedVersion)
        {
            map.Get("version")?.Error($"対応していない版です（{v}）。対応している版は {SupportedVersion} です");
            return false;
        }

        return version != null;
    }

    /// <summary>app.yaml を読む。</summary>
    public static AppConfig? ParseApp(YNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        CheckVersion(map);
        var name = map.Str("name", required: true);
        if (name != null && !NamePattern().IsMatch(name))
        {
            map.Get("name")?.Error("名前は小文字の英数字、- と _ で書いてください");
        }

        var platform = map.Enum("platform", true, "web", "unity", "unreal", "custom");
        var criticality = map.Enum("criticality", true, "low", "medium", "high");
        var requiredText = map.Str("required_maturity", required: true);
        var required = MaturityLevel.Zero;
        if (requiredText != null && !MaturityLevel.TryParse(requiredText, out required))
        {
            map.Get("required_maturity")?.Error("0、1、2、3、3a、3b、3c のどれかを書いてください");
        }

        // test_api を書かない場合は「ない」として扱う（アプリ内の実装を必須にしない。第24.6節）
        var testApi = map.Enum("test_api", false, "none", "http") ?? "none";
        var testApiPort = (int)(map.Whole("test_api_port", false, 1, 65535) ?? 3999);
        var adapter = map.Str("adapter") ?? "web-socketio";
        var description = map.Str("description");
        var capabilities = new Dictionary<string, string>(StringComparer.Ordinal);
        if (map.Map("capabilities") is { } caps)
        {
            foreach (var (key, value) in caps.Entries())
            {
                var version = value.AsString();
                if (version != null)
                {
                    capabilities[key] = version;
                }
            }

            caps.Done();
        }

        map.Done();
        if (name == null || platform == null || criticality == null)
        {
            return null;
        }

        return new AppConfig(
            name,
            platform switch { "web" => Platform.Web, "unity" => Platform.Unity, "unreal" => Platform.Unreal, _ => Platform.Custom },
            criticality switch { "low" => Criticality.Low, "medium" => Criticality.Medium, _ => Criticality.High },
            required,
            testApi == "http" ? TestApiMode.Http : TestApiMode.None,
            testApiPort,
            adapter,
            capabilities,
            description);
    }

    /// <summary>state.yaml を読む。</summary>
    public static StateConfig? ParseState(YNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        CheckVersion(map);
        var keys = new Dictionary<string, StateKey>(StringComparer.Ordinal);
        var stateMap = map.Map("state", required: true);
        if (stateMap != null)
        {
            foreach (var (key, value) in stateMap.Entries())
            {
                if (!StateKeyPattern().IsMatch(key))
                {
                    value.Error("状態のキーは「対象.属性」の形で、小文字の英数字と _ で書いてください（例：player.hand）");
                }

                var entry = ParseStateKey(key, value);
                if (entry != null)
                {
                    keys[key] = entry;
                }
            }

            stateMap.Done();
            if (keys.Count == 0)
            {
                stateMap.Owner.Error("状態のキーを1つ以上書いてください");
            }
        }

        var determinism = DeterminismConfig.Strict;
        if (map.Map("determinism") is { } det)
        {
            var volatileKeys = det.StrList("volatile");
            var idNormalization = "none";
            IReadOnlyList<string> sort = [];
            if (det.Map("normalize") is { } normalize)
            {
                idNormalization = normalize.Enum("ids", false, "none", "creation_order") ?? "none";
                sort = normalize.StrList("sort");
                normalize.Done();
            }

            var honestRuns = 3;
            var undeclared = "report";
            if (det.Map("noise_check") is { } noise)
            {
                honestRuns = (int)(noise.Whole("honest_runs", false, 2, 100) ?? 3);
                undeclared = noise.Enum("undeclared_noise", false, "report", "ignore") ?? "report";
                noise.Done();
            }

            var envelopeRuns = (int)(det.Whole("envelope_runs", false, 2, 100_000) ?? 39);
            det.Done();
            determinism = new DeterminismConfig(volatileKeys, idNormalization, sort, honestRuns, undeclared, envelopeRuns);
        }

        map.Done();
        return stateMap == null ? null : new StateConfig(keys, determinism);
    }

    private static StateKey? ParseStateKey(string key, YNode node)
    {
        var map = node.AsMap();
        if (map == null)
        {
            return null;
        }

        var typeText = map.Enum("type", true, "int", "float", "bool", "string", "id", "list", "map", "vec3", "timestamp", "json");
        var per = map.Str("per", required: true);
        var description = map.Str("description");
        StateSource? source = null;
        var sourceNode = map.Get("source", required: true);
        if (sourceNode != null && sourceNode.AsMap() is { } sourceMap)
        {
            var kinds = SourceKinds.Where(sourceMap.Has).ToList();
            if (kinds.Count != 1)
            {
                sourceNode.Error("source には sql、socket、dom、adapter のどれか1つだけを書いてください");
            }

            if (sourceMap.Has("sql"))
            {
                var sql = sourceMap.Str("sql", required: true);
                if (sql != null)
                {
                    source = new SqlSource(sql);
                }
            }

            if (sourceMap.Has("socket"))
            {
                var socket = sourceMap.Map("socket", required: true);
                if (socket != null)
                {
                    var events = new List<string>();
                    if (socket.Str("event") is { } single)
                    {
                        events.Add(single);
                    }

                    events.AddRange(socket.StrList("events"));
                    var path = socket.Str("path", required: true);
                    if (events.Count == 0)
                    {
                        socket.Owner.Error("event か events を書いてください");
                    }

                    if (path != null && !path.StartsWith('$'))
                    {
                        socket.Get("path")?.Error("path は $ で始まる JSONPath で書いてください");
                    }

                    socket.Done();
                    if (path != null && events.Count > 0)
                    {
                        source = new SocketSource(events, path);
                    }
                }
            }

            if (sourceMap.Has("dom"))
            {
                var dom = sourceMap.Map("dom", required: true);
                if (dom != null)
                {
                    var selector = dom.Str("selector", required: true);
                    var read = dom.Str("read") ?? "text";
                    if (read is not ("text" or "value" or "count" or "exists") && !read.StartsWith("attr:", StringComparison.Ordinal))
                    {
                        dom.Get("read")?.Error("read は text、value、count、exists、attr:<名前> のどれかにしてください");
                    }

                    var page = dom.Str("page");
                    dom.Done();
                    if (selector != null)
                    {
                        source = new DomSource(selector, read, page);
                    }
                }
            }

            if (sourceMap.Has("adapter"))
            {
                var adapterKey = sourceMap.Str("adapter", required: true);
                if (adapterKey != null)
                {
                    source = new AdapterSource(adapterKey);
                }
            }

            sourceMap.Done();
        }

        map.Done();
        if (typeText == null || per == null || source == null)
        {
            return null;
        }

        var type = typeText switch
        {
            "int" => StateType.Integral,
            "float" => StateType.Real,
            "bool" => StateType.Bool,
            "string" => StateType.Text,
            "id" => StateType.Id,
            "list" => StateType.List,
            "map" => StateType.Map,
            "vec3" => StateType.Vec3,
            "timestamp" => StateType.Timestamp,
            _ => StateType.Json,
        };
        return new StateKey(key, type, per, source, description);
    }

    /// <summary>authority.yaml を読む。</summary>
    public static AuthorityConfig? ParseAuthority(YNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        CheckVersion(map);
        var actors = map.StrList("actors", required: true);

        // 宣言のない状態キーは最も厳しい扱い（サーバー権威・完全な検証・影響範囲は経済）
        var defaults = new AuthorityEntry("*", Authority.Server, new ValidationSpec(ValidationKind.Full, new Dictionary<string, double>()), Scope.Economy, null, false);
        if (map.Map("defaults") is { } defaultsMap)
        {
            defaults = ParseAuthorityEntry("*", defaultsMap, defaults) with { Declared = false };
            defaultsMap.Done();
        }

        var state = new Dictionary<string, AuthorityEntry>(StringComparer.Ordinal);
        if (map.Map("state") is { } stateMap)
        {
            foreach (var (key, value) in stateMap.Entries())
            {
                if (value.AsMap() is { } entryMap)
                {
                    state[key] = ParseAuthorityEntry(key, entryMap, defaults);
                    entryMap.Done();
                }
            }

            stateMap.Done();
        }

        var ownership = new Dictionary<string, OwnershipEntry>(StringComparer.Ordinal);
        if (map.Map("ownership") is { } ownershipMap)
        {
            foreach (var (entity, value) in ownershipMap.Entries())
            {
                if (value.AsMap() is not { } entry)
                {
                    continue;
                }

                var owner = entry.Str("owner", required: true);
                var read = entry.StrList("read", required: true);
                var write = entry.StrList("write", required: true);
                foreach (var actor in read.Concat(write))
                {
                    if (actors.Count > 0 && !actors.Contains(actor, StringComparer.Ordinal) && actor != "owner")
                    {
                        entry.Owner.Error($"actors にない利用者の種類です：{actor}");
                    }
                }

                entry.Done();
                if (owner != null)
                {
                    ownership[entity] = new OwnershipEntry(entity, owner, read, write);
                }
            }

            ownershipMap.Done();
        }

        var inputs = new Dictionary<string, InputEntry>(StringComparer.Ordinal);
        if (map.Map("inputs") is { } inputsMap)
        {
            foreach (var (name, value) in inputsMap.Entries())
            {
                var parts = name.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    value.Error("キーは「event <イベント名>」か「<メソッド> <パス>」の形で書いてください");
                    continue;
                }

                var kind = parts[0] == "event" ? "event" : "http";
                if (kind == "http" && parts[0] is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
                {
                    value.Error("HTTP のメソッドは GET、POST、PUT、PATCH、DELETE のどれかにしてください");
                }

                if (value.AsMap() is not { } entry)
                {
                    continue;
                }

                var accepts = entry.StrList("accepts", required: true);
                entry.Done();
                inputs[name] = new InputEntry(name, kind, parts[1], accepts);
            }

            inputsMap.Done();
        }

        var phases = new Dictionary<string, PhaseEntry>(StringComparer.Ordinal);
        if (map.Map("phases") is { } phasesMap)
        {
            foreach (var (phase, value) in phasesMap.Entries())
            {
                if (value.AsMap() is not { } entry)
                {
                    continue;
                }

                var allowed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                if (entry.Map("allowed", required: true) is { } allowedMap)
                {
                    foreach (var (role, events) in allowedMap.Entries())
                    {
                        allowed[role] = events.IsNull ? [] : events.AsStringList();
                    }

                    allowedMap.Done();
                }

                entry.Done();
                phases[phase] = new PhaseEntry(phase, allowed);
            }

            phasesMap.Done();
        }

        var sent = new Dictionary<string, SentSchema>(StringComparer.Ordinal);
        if (map.Map("sent") is { } sentMap)
        {
            foreach (var (eventName, value) in sentMap.Entries())
            {
                if (value.AsMap() is not { } entry)
                {
                    continue;
                }

                var paths = entry.StrList("paths", required: true);
                foreach (var path in paths)
                {
                    if (!path.StartsWith('$'))
                    {
                        entry.Owner.Error($"paths は $ で始まる JSONPath で書いてください：{path}");
                    }
                }

                entry.Done();
                sent[eventName] = new SentSchema(eventName, paths);
            }

            sentMap.Done();
        }

        map.Done();
        return new AuthorityConfig(actors, defaults, state, ownership, inputs, phases, sent);
    }

    private static AuthorityEntry ParseAuthorityEntry(string key, YMap map, AuthorityEntry defaults)
    {
        var authorityText = map.Enum("authority", false, "server", "client", "host");
        var authority = authorityText switch
        {
            "server" => Authority.Server,
            "client" => Authority.Client,
            "host" => Authority.Host,
            _ => defaults.Authority,
        };

        var validation = defaults.Validation;
        if (map.Get("validation") is { } validationNode && !validationNode.IsNull)
        {
            if (validationNode.IsScalar)
            {
                var kind = validationNode.AsEnum("none", "plausibility", "full");
                validation = new ValidationSpec(ToValidationKind(kind) ?? defaults.Validation.Kind, new Dictionary<string, double>());
            }
            else if (validationNode.AsMap() is { } validationMap)
            {
                var kind = validationMap.Enum("kind", true, "none", "plausibility", "full");
                var parameters = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (name, value) in validationMap.Entries())
                {
                    if (name == "kind")
                    {
                        continue;
                    }

                    if (name.EndsWith("_ms", StringComparison.Ordinal) || value.AsString() is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        if (value.AsDouble() is { } number)
                        {
                            parameters[name] = number;
                        }
                    }
                    else
                    {
                        value.Error("検証のパラメータは数値で書いてください");
                    }
                }

                validation = new ValidationSpec(ToValidationKind(kind) ?? defaults.Validation.Kind, parameters);
            }
        }

        var scopeText = map.Enum("scope", false, "self", "visible_to_others", "persisted", "economy");
        var scope = scopeText switch
        {
            "self" => Scope.Self,
            "visible_to_others" => Scope.VisibleToOthers,
            "persisted" => Scope.Persisted,
            "economy" => Scope.Economy,
            _ => defaults.Scope,
        };
        var visibilityText = map.Enum("visibility", false, "all", "members", "owner", "none");
        Visibility? visibility = visibilityText switch
        {
            "all" => Visibility.All,
            "members" => Visibility.Members,
            "owner" => Visibility.Owner,
            "none" => Visibility.None,
            _ => defaults.Visibility,
        };
        return new AuthorityEntry(key, authority, validation, scope, visibility, true);
    }

    private static ValidationKind? ToValidationKind(string? text) => text switch
    {
        "none" => ValidationKind.None,
        "plausibility" => ValidationKind.Plausibility,
        "full" => ValidationKind.Full,
        _ => null,
    };

    /// <summary>invariants.yaml を読む。</summary>
    public static InvariantsConfig? ParseInvariants(YNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        CheckVersion(map);
        var list = new List<InvariantSpec>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var node = map.Get("invariants", required: true);
        if (node != null)
        {
            foreach (var item in node.AsList())
            {
                if (item.AsMap() is not { } entry)
                {
                    continue;
                }

                var id = entry.Str("id", required: true);
                if (id != null && !ids.Add(id))
                {
                    entry.Owner.Error($"id が重複しています：{id}");
                }

                var severity = entry.Enum("severity", true, "critical", "high", "medium", "low");
                var stage = (int)(entry.Whole("stage", true, 1, 3) ?? 1);
                var category = entry.Str("category");
                var description = entry.Str("description");
                var exceptions = entry.StrList("exceptions");
                var kinds = InvariantKinds.Where(entry.Has).ToList();
                if (kinds.Count != 1)
                {
                    entry.Owner.Error("sql、sent_sql、cross_sql、delta、builtin のどれか1つだけを書いてください");
                }

                InvariantKind kind = InvariantKind.Sql;
                string? query = null;
                string? deltaKey = null;
                double? maxPerSecond = null;
                string? builtin = null;
                var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
                if (entry.Has("sql"))
                {
                    kind = InvariantKind.Sql;
                    query = entry.Str("sql", required: true);
                }
                else if (entry.Has("sent_sql"))
                {
                    kind = InvariantKind.SentSql;
                    query = entry.Str("sent_sql", required: true);
                }
                else if (entry.Has("cross_sql"))
                {
                    kind = InvariantKind.CrossSql;
                    query = entry.Str("cross_sql", required: true);
                }
                else if (entry.Has("delta"))
                {
                    kind = InvariantKind.Delta;
                    if (entry.Map("delta", required: true) is { } delta)
                    {
                        deltaKey = delta.Str("key", required: true);
                        maxPerSecond = delta.Number("max_per_second", true, 0);
                        delta.Done();
                    }
                }
                else if (entry.Has("builtin"))
                {
                    kind = InvariantKind.Builtin;
                    builtin = entry.Str("builtin", required: true);
                }

                if (entry.Map("params") is { } paramMap)
                {
                    foreach (var (name, value) in paramMap.Entries())
                    {
                        if (value.AsString() is { } text)
                        {
                            parameters[name] = text;
                        }
                    }

                    paramMap.Done();
                }

                entry.Done();
                if (id == null || severity == null)
                {
                    continue;
                }

                list.Add(new InvariantSpec(
                    id,
                    ParseSeverity(severity),
                    stage,
                    category,
                    kind,
                    query,
                    deltaKey,
                    maxPerSecond,
                    exceptions,
                    builtin,
                    parameters,
                    description));
            }
        }

        map.Done();
        return new InvariantsConfig(list);
    }

    /// <summary>重大度の文字列を読む。</summary>
    public static Severity ParseSeverity(string text) => text switch
    {
        "critical" => Severity.Critical,
        "high" => Severity.High,
        "medium" => Severity.Medium,
        _ => Severity.Low,
    };

    /// <summary>security.yaml を読む。</summary>
    public static SecurityConfig? ParseSecurity(YNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        CheckVersion(map);
        var known = KnownGoals;
        var goals = map.StrList("goals", required: true);
        foreach (var goal in goals)
        {
            if (!known.Contains(goal))
            {
                map.Get("goals")?.Error($"知らない攻撃目標です：{goal}（{string.Join(", ", known)}）");
            }
        }

        var notApplicable = map.StrList("not_applicable");
        PiiFlags? pii = null;
        string? secretFile = null;
        var secretKeys = new List<string>();
        if (map.Map("flags") is { } flags)
        {
            if (flags.Map("pii") is { } piiMap)
            {
                var table = piiMap.Str("table", required: true);
                var columns = piiMap.StrList("columns", required: true);
                var count = (int)(piiMap.Whole("count", true, 1, 100_000) ?? 0);
                piiMap.Done();
                if (table != null)
                {
                    pii = new PiiFlags(table, columns, count);
                }
            }

            if (flags.Map("secret") is { } secretMap)
            {
                secretFile = secretMap.Str("file", required: true);
                secretMap.Done();
            }

            secretKeys.AddRange(flags.StrList("state_keys"));
            flags.Done();
        }

        var xss = true;
        var sqli = true;
        if (map.Map("markers") is { } markers)
        {
            xss = markers.Bool("xss") ?? true;
            sqli = markers.Bool("sqli") ?? true;
            markers.Done();
        }

        map.Done();
        return new SecurityConfig(goals, notApplicable, pii, secretFile, secretKeys, xss, sqli);
    }

    /// <summary>environment.yaml を読む。</summary>
    public static EnvironmentConfig? ParseEnvironment(YNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        CheckVersion(map);
        var project = map.Str("project") ?? "app";
        if (!NamePattern().IsMatch(project))
        {
            map.Get("project")?.Error("project は小文字の英数字、- と _ で書いてください");
        }

        string? publicHost = null;
        var publicPort = 80;
        if (map.Map("public_endpoint", required: true) is { } endpoint)
        {
            publicHost = endpoint.Str("host", required: true);
            publicPort = (int)(endpoint.Whole("port", false, 1, 65535) ?? 80);
            endpoint.Done();
        }

        AppNodeConfig? app = null;
        if (map.Map("app", required: true) is { } appMap)
        {
            app = ParseAppNode(appMap);
            appMap.Done();
        }

        var nodes = new NodeLayout(1, 1, 1, true);
        if (map.Map("nodes") is { } nodesMap)
        {
            nodes = new NodeLayout(
                (int)(nodesMap.Whole("honest_clients", false, 0, 64) ?? 1),
                (int)(nodesMap.Whole("attacker_clients", false, 0, 64) ?? 1),
                (int)(nodesMap.Whole("browsers", false, 0, 8) ?? 1),
                nodesMap.Bool("sandbox") ?? true);
            nodesMap.Done();
        }

        var baseImages = new Dictionary<string, string>(StringComparer.Ordinal);
        if (map.Map("base_images") is { } imagesMap)
        {
            foreach (var (name, value) in imagesMap.Entries())
            {
                if (value.AsString() is { } reference)
                {
                    if (!reference.Contains("@sha256:", StringComparison.Ordinal))
                    {
                        value.Error("イメージはダイジェスト（@sha256:...）で固定してください（指示書 制約10）");
                    }

                    baseImages[name] = reference;
                }
            }

            imagesMap.Done();
        }

        var egress = map.StrList("egress_allow");
        var profiles = new Dictionary<string, ResourceProfile>(StringComparer.Ordinal);
        if (map.Map("resource_profiles") is { } profilesMap)
        {
            foreach (var (name, value) in profilesMap.Entries())
            {
                if (value.AsMap() is { } profileMap)
                {
                    profiles[name] = ParseProfile(name, profileMap);
                    profileMap.Done();
                }
            }

            profilesMap.Done();
        }

        map.Done();
        if (publicHost == null || app == null)
        {
            return null;
        }

        return new EnvironmentConfig(project, publicHost, publicPort, app, nodes, baseImages, egress, profiles);
    }

    private static AppNodeConfig? ParseAppNode(YMap map)
    {
        string? context = null;
        var dockerfile = "Dockerfile";
        if (map.Map("build", required: true) is { } build)
        {
            context = build.Str("context", required: true);
            dockerfile = build.Str("dockerfile") ?? "Dockerfile";
            build.Done();
        }

        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        if (map.Map("image_pins") is { } pinMap)
        {
            foreach (var (image, value) in pinMap.Entries())
            {
                if (value.AsString() is { } digest)
                {
                    if (!digest.StartsWith("sha256:", StringComparison.Ordinal))
                    {
                        value.Error("ダイジェストは sha256: で始めてください");
                    }

                    pins[image] = digest;
                }
            }

            pinMap.Done();
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (map.Map("environment") is { } envMap)
        {
            foreach (var (name, value) in envMap.Entries())
            {
                env[name] = value.AsString() ?? string.Empty;
            }

            envMap.Done();
        }

        var command = map.StrList("command");
        var port = (int)(map.Whole("port", true, 1, 65535) ?? 0);
        var serverPort = (int)(map.Whole("server_port", false, 1, 65535) ?? port);
        var dataDir = map.Str("data_dir", required: true);
        var dbFile = map.Str("db_file", required: true);
        var health = map.Str("health_path") ?? "/";
        var socketPath = map.Str("socketio_path") ?? "/socket.io/";
        var user = map.Str("user");
        var testBuild = map.Str("test_build_dockerfile");
        var testApiEnable = map.Str("test_api_enable");
        if (context == null || dataDir == null || dbFile == null)
        {
            return null;
        }

        return new AppNodeConfig(context, dockerfile, pins, env, command, port, serverPort, dataDir, dbFile, health, socketPath, user, testBuild, testApiEnable);
    }

    private static ResourceProfile ParseProfile(string name, YMap map)
    {
        var target = map.Enum("target", true, "local", "verification_env") ?? "local";
        var nodes = ParseNodeResources(map.Map("nodes"));
        var machines = ParseNodeResources(map.Map("machines"));
        NetworkConditions? network = null;
        if (map.Map("network") is { } net)
        {
            network = new NetworkConditions(net.BitsPerSecond("bandwidth"), net.Duration("latency"), net.Duration("jitter"), net.Ratio("loss"));
            net.Done();
        }

        var validity = ValidityCriteria.Default;
        if (map.Map("validity") is { } v)
        {
            var d = ValidityCriteria.Default;
            var swap = v.Enum("swap", false, "none", "allowed");
            validity = new ValidityCriteria(
                v.Ratio("load_cpu_max") ?? d.LoadCpuMax,
                v.Ratio("send_lag_p99_max") ?? d.SendLagP99Max,
                v.Ratio("reference_drift_max") ?? d.ReferenceDriftMax,
                swap == "allowed",
                v.Ratio("cpu_steal_max") ?? d.CpuStealMax,
                v.Bool("steady_state_check") ?? d.SteadyStateCheck,
                (int)(v.Whole("runs", false, 1, 1000) ?? d.Runs),
                v.Ratio("detect_regression") ?? d.DetectRegression,
                v.Ratio("max_cv") ?? d.MaxCv);
            v.Done();
        }

        return new ResourceProfile(name, target, nodes, machines, network, validity);
    }

    private static Dictionary<string, NodeResources> ParseNodeResources(YMap? map)
    {
        var result = new Dictionary<string, NodeResources>(StringComparer.Ordinal);
        if (map == null)
        {
            return result;
        }

        foreach (var (node, value) in map.Entries())
        {
            if (value.AsMap() is not { } entry)
            {
                continue;
            }

            var resources = new NodeResources(
                entry.Number("cpus", false, 0.01, 1024),
                entry.Str("cpuset"),
                entry.Bytes("memory"),
                entry.Whole("disk_iops", false, 1, 10_000_000));
            entry.Done();
            result[node] = resources;
        }

        map.Done();
        return result;
    }

    /// <summary>performance.yaml を読む。</summary>
    public static PerformanceConfig? ParsePerformance(YNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        CheckVersion(map);
        var slos = new List<SloSpec>();
        if (map.Get("slo") is { } sloNode && !sloNode.IsNull)
        {
            foreach (var item in sloNode.AsList())
            {
                if (item.AsMap() is not { } entry)
                {
                    continue;
                }

                var id = entry.Str("id", required: true);
                var profile = entry.Str("profile", required: true);
                var scenario = entry.Str("scenario", required: true);
                var users = (int)(entry.Whole("concurrent_users", false, 1, 10_000_000) ?? 1);
                var metric = entry.Str("metric", required: true);
                var thresholdText = entry.Str("threshold", required: true);
                double threshold = 0;
                var unit = "value";
                if (thresholdText != null)
                {
                    if (Units.TryParseDuration(thresholdText, out var duration) && !double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        threshold = duration.TotalMilliseconds;
                        unit = "ms";
                    }
                    else if (thresholdText.EndsWith('%') && Units.TryParseRatio(thresholdText, out var ratio))
                    {
                        threshold = ratio;
                        unit = "ratio";
                    }
                    else if (double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        threshold = number;
                    }
                    else
                    {
                        entry.Get("threshold")?.Error("閾値を読めません（例：200ms、0.1%、1.2）");
                    }
                }

                entry.Done();
                if (id != null && profile != null && scenario != null && metric != null)
                {
                    slos.Add(new SloSpec(id, profile, scenario, users, metric, threshold, unit));
                }
            }
        }

        var factors = new List<CalibrationFactorSpec>();
        if (map.Get("calibration") is { } calibrationNode && !calibrationNode.IsNull)
        {
            foreach (var item in calibrationNode.AsList())
            {
                if (item.AsMap() is not { } entry)
                {
                    continue;
                }

                var from = entry.Str("from", required: true);
                var to = entry.Str("to", required: true);
                var scenario = entry.Str("scenario", required: true);
                var metric = entry.Str("metric") ?? "max_users_within_slo";
                var factor = entry.Number("factor", true, 0) ?? 0;
                var range = entry.Get("factor_range", required: true);
                double low = factor;
                double high = factor;
                if (range != null)
                {
                    var values = range.AsList();
                    if (values.Count != 2)
                    {
                        range.Error("factor_range は [低い側, 高い側] の2つで書いてください");
                    }
                    else
                    {
                        low = values[0].AsDouble(0) ?? factor;
                        high = values[1].AsDouble(0) ?? factor;
                        if (low > high)
                        {
                            range.Error("factor_range は低い側を先に書いてください");
                        }
                    }
                }

                var bottleneck = entry.Str("bottleneck", required: true);
                var measured = entry.Date("measured_at", required: true);
                var validUntil = entry.Date("valid_until", required: true);
                var maxError = entry.Ratio("max_prediction_error") ?? 0.20;
                var invalidatedBy = entry.StrList("invalidated_by");
                entry.Done();
                if (from != null && to != null && scenario != null && bottleneck != null && measured is { } m && validUntil is { } u)
                {
                    factors.Add(new CalibrationFactorSpec(from, to, scenario, metric, factor, low, high, bottleneck, m, u, maxError, invalidatedBy));
                }
            }
        }

        map.Done();
        return new PerformanceConfig(slos, factors);
    }

    /// <summary>sampling.yaml を読む。</summary>
    public static SamplingConfig? ParseSampling(YNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        CheckVersion(map);
        var confidence = map.Ratio("confidence") ?? 0.95;
        if (confidence is <= 0 or >= 1)
        {
            map.Get("confidence")?.Error("信頼水準は 0% より大きく 100% より小さくしてください");
        }

        var unit = map.Enum("unit", false, "check_target", "report") ?? "check_target";
        var categories = new Dictionary<string, SamplingCategory>(StringComparer.Ordinal);
        if (map.Map("categories", required: true) is { } categoriesMap)
        {
            foreach (var (name, value) in categoriesMap.Entries())
            {
                if (value.AsMap() is not { } entry)
                {
                    continue;
                }

                var lossRatio = entry.Number("loss_ratio", false, 0);
                var errorUpperNode = entry.Get("error_upper", required: true);
                var mode = ErrorUpperMode.Auto;
                double? fixedUpper = null;
                if (errorUpperNode != null)
                {
                    var text = errorUpperNode.AsString();
                    if (text == "auto")
                    {
                        mode = ErrorUpperMode.Auto;
                    }
                    else if (text == "none")
                    {
                        mode = ErrorUpperMode.None;
                    }
                    else if (text != null && Units.TryParseRatio(text, out var ratio))
                    {
                        mode = ErrorUpperMode.Fixed;
                        fixedUpper = ratio;
                    }
                    else
                    {
                        errorUpperNode.Error("error_upper は auto、none、または割合（例：0.1%）で書いてください");
                    }
                }

                var fallback = entry.Ratio("fallback_error_upper");
                if (mode == ErrorUpperMode.Auto && fallback == null)
                {
                    // r が未定の間に使う値がないと上限を出せない。書き忘れは厳しい側に倒す（0.1%）。
                    fallback = 0.001;
                }

                entry.Done();
                categories[name] = new SamplingCategory(name, lossRatio, mode, fixedUpper, fallback);
            }

            categoriesMap.Done();
        }

        var verifierRole = "verifier";
        var blind = true;
        if (map.Map("verifier") is { } verifier)
        {
            verifierRole = verifier.Str("role") ?? "verifier";
            blind = verifier.Bool("blind") ?? true;
            verifier.Done();
        }

        var minSensitivity = map.Number("min_verifier_sensitivity", false, 0, 1) ?? 0.7;
        var audit = 50;
        if (map.Map("human") is { } human)
        {
            audit = (int)(human.Whole("audit_per_release", false, 0, 100_000) ?? 50);
            human.Done();
        }

        var freeShare = map.Ratio("free_exploration_share") ?? 0.20;
        map.Done();
        return new SamplingConfig(confidence, unit, categories, verifierRole, blind, minSensitivity, audit, freeShare);
    }

    /// <summary>agents.yaml を読む。</summary>
    public static AgentsConfig? ParseAgents(YNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var map = root.AsMap();
        if (map == null)
        {
            return null;
        }

        CheckVersion(map);
        var defaultAgent = "claude-code";
        string? defaultModel = null;
        if (map.Map("defaults") is { } defaults)
        {
            defaultAgent = defaults.Str("agent") ?? "claude-code";
            defaultModel = defaults.Str("model");
            defaults.Done();
        }

        var roles = new Dictionary<string, RoleAssignment>(StringComparer.Ordinal);
        if (map.Map("roles", required: true) is { } rolesMap)
        {
            foreach (var (role, value) in rolesMap.Entries())
            {
                if (value.AsMap() is not { } entry)
                {
                    continue;
                }

                var agent = entry.Str("agent") ?? defaultAgent;
                var model = entry.Str("model") ?? defaultModel;
                entry.Done();
                roles[role] = new RoleAssignment(role, agent, model);
            }

            rolesMap.Done();
        }

        var differ = new List<IReadOnlyList<string>>();
        if (map.Get("constraints") is { } constraints && !constraints.IsNull)
        {
            foreach (var item in constraints.AsList())
            {
                if (item.AsMap() is not { } entry)
                {
                    continue;
                }

                var pair = entry.StrList("differ", required: true);
                if (pair.Count < 2)
                {
                    entry.Owner.Error("differ には役割を2つ以上書いてください");
                }

                foreach (var role in pair)
                {
                    if (!roles.ContainsKey(role))
                    {
                        entry.Owner.Error($"roles にない役割です：{role}");
                    }
                }

                entry.Done();
                differ.Add(pair);
            }
        }

        var onViolation = map.Enum("on_violation", false, "warn", "error") ?? "warn";
        var budget = map.Number("max_budget_usd_per_run", false, 0);
        map.Done();
        return new AgentsConfig(defaultAgent, defaultModel, roles, differ, onViolation, budget);
    }
}
