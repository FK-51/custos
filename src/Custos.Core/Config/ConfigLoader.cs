using Custos.Core.Util;

namespace Custos.Core.Config;

/// <summary>読み込んだ設定一式。ハッシュは結果に記録する（設計書 第12.6節、第17.4節）。</summary>
public sealed record ConfigSet(
    string Directory,
    AppConfig App,
    StateConfig State,
    AuthorityConfig Authority,
    EnvironmentConfig Environment,
    InvariantsConfig? Invariants,
    SecurityConfig? Security,
    PerformanceConfig? Performance,
    SamplingConfig? Sampling,
    AgentsConfig? Agents,
    string Hash,
    IReadOnlyDictionary<string, string> FileHashes);

/// <summary>読み込みの結果。書式の誤り（A-2）と参照の誤り（A-4）を分けて持つ。</summary>
public sealed record ConfigLoadResult(
    ConfigSet? Config,
    IReadOnlyList<ConfigError> FormatErrors,
    IReadOnlyList<ConfigError> ReferenceErrors,
    IReadOnlyList<string> Warnings)
{
    /// <summary>書式も参照も正しいか。</summary>
    public bool Ok => Config != null && FormatErrors.Count == 0 && ReferenceErrors.Count == 0;

    /// <summary>誤りがあれば例外にする。</summary>
    public ConfigSet Require()
    {
        if (!Ok || Config == null)
        {
            var lines = FormatErrors.Concat(ReferenceErrors).Select(e => "  " + e);
            throw new CustosException("設定ファイルに誤りがあります:\n" + string.Join("\n", lines));
        }

        return Config;
    }
}

/// <summary>検証用リポジトリの config/ を読む。</summary>
public static class ConfigLoader
{
    /// <summary>段階0で必須のファイル（指示書 タスク2）。</summary>
    public static readonly IReadOnlyList<string> RequiredFiles = ["app.yaml", "state.yaml", "environment.yaml", "authority.yaml"];

    /// <summary>書式を定めているファイル（設計書 第18.1節）。</summary>
    public static readonly IReadOnlyList<string> KnownFiles =
    [
        "app.yaml", "state.yaml", "authority.yaml", "invariants.yaml", "security.yaml",
        "environment.yaml", "performance.yaml", "sampling.yaml", "agents.yaml",
    ];

    /// <summary>ディレクトリの設定一式を読む。</summary>
    public static ConfigLoadResult Load(string directory)
    {
        var errors = new ConfigErrors();
        var warnings = new List<string>();
        var fileHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!System.IO.Directory.Exists(directory))
        {
            errors.Add(directory, string.Empty, "設定のディレクトリがありません");
            return new ConfigLoadResult(null, errors.Items, [], warnings);
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.yaml"))
        {
            var name = Path.GetFileName(file);
            if (!KnownFiles.Contains(name, StringComparer.Ordinal))
            {
                errors.Add(name, string.Empty, "書式を定めていないファイルです（第18.1節の一覧にない）");
            }
        }

        YNode? Read(string name, bool required)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                if (required)
                {
                    errors.Add(name, string.Empty, "必須の設定ファイルがありません");
                }

                return null;
            }

            var text = File.ReadAllText(path);
            fileHashes[name] = Hashing.Sha256Hex(text);
            return YamlDocument.Parse(text, name, errors);
        }

        var appNode = Read("app.yaml", true);
        var stateNode = Read("state.yaml", true);
        var envNode = Read("environment.yaml", true);
        var authorityNode = Read("authority.yaml", true);
        var invariantsNode = Read("invariants.yaml", false);
        var securityNode = Read("security.yaml", false);
        var performanceNode = Read("performance.yaml", false);
        var samplingNode = Read("sampling.yaml", false);
        var agentsNode = Read("agents.yaml", false);

        var app = appNode == null ? null : ConfigParsers.ParseApp(appNode);
        var state = stateNode == null ? null : ConfigParsers.ParseState(stateNode);
        var environment = envNode == null ? null : ConfigParsers.ParseEnvironment(envNode);
        var authority = authorityNode == null ? null : ConfigParsers.ParseAuthority(authorityNode);
        var invariants = invariantsNode == null ? null : ConfigParsers.ParseInvariants(invariantsNode);
        var security = securityNode == null ? null : ConfigParsers.ParseSecurity(securityNode);
        var performance = performanceNode == null ? null : ConfigParsers.ParsePerformance(performanceNode);
        var sampling = samplingNode == null ? null : ConfigParsers.ParseSampling(samplingNode);
        var agents = agentsNode == null ? null : ConfigParsers.ParseAgents(agentsNode);

        if (app == null || state == null || environment == null || authority == null)
        {
            if (!errors.Any)
            {
                errors.Add(directory, string.Empty, "必須の設定を読めませんでした");
            }

            return new ConfigLoadResult(null, errors.Items, [], warnings);
        }

        var hash = Hashing.Combine(fileHashes);
        var config = new ConfigSet(directory, app, state, authority, environment, invariants, security, performance, sampling, agents, hash, fileHashes);
        var references = CheckReferences(config, warnings);
        return new ConfigLoadResult(config, errors.Items, references, warnings);
    }

    /// <summary>
    /// 参照しているキーが実在するかを照合する（設計書 第14.8節、第17.3節 A-4）。
    /// 権威マップ、データの所有者、不変条件、決定性の宣言、フラグ、SLO、検証役の役割。
    /// </summary>
    public static IReadOnlyList<ConfigError> CheckReferences(ConfigSet config, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(warnings);
        var errors = new ConfigErrors();
        var keys = config.State.Keys;

        void Need(string file, string path, string key)
        {
            if (!keys.ContainsKey(key))
            {
                errors.Add(file, path, $"state.yaml にないキーを参照しています：{key}");
            }
        }

        foreach (var key in config.Authority.State.Keys)
        {
            Need("authority.yaml", $"state.{key}", key);
        }

        foreach (var entry in config.Authority.Ownership.Values)
        {
            Need("authority.yaml", $"ownership.{entry.Entity}.owner", entry.OwnerKey);
        }

        foreach (var key in config.State.Determinism.Volatile)
        {
            Need("state.yaml", "determinism.volatile", key);
        }

        foreach (var key in config.State.Determinism.Sort)
        {
            Need("state.yaml", "determinism.normalize.sort", key);
        }

        if (config.Invariants != null)
        {
            foreach (var invariant in config.Invariants.Invariants)
            {
                if (invariant.DeltaKey != null)
                {
                    Need("invariants.yaml", $"invariants.{invariant.Id}.delta.key", invariant.DeltaKey);
                }
            }
        }

        if (config.Security != null)
        {
            foreach (var key in config.Security.SecretStateKeys)
            {
                Need("security.yaml", "flags.state_keys", key);
            }
        }

        if (config.Performance != null)
        {
            foreach (var slo in config.Performance.Slo)
            {
                if (!config.Environment.ResourceProfiles.ContainsKey(slo.Profile))
                {
                    errors.Add("performance.yaml", $"slo.{slo.Id}.profile", $"environment.yaml にないプロファイルです：{slo.Profile}");
                }
            }

            foreach (var factor in config.Performance.Calibration)
            {
                foreach (var profile in (string[])[factor.From, factor.To])
                {
                    if (!config.Environment.ResourceProfiles.ContainsKey(profile))
                    {
                        errors.Add("performance.yaml", "calibration", $"environment.yaml にないプロファイルです：{profile}");
                    }
                }
            }
        }

        if (config.Sampling != null && config.Agents != null && !config.Agents.Roles.ContainsKey(config.Sampling.VerifierRole))
        {
            errors.Add("sampling.yaml", "verifier.role", $"agents.yaml にない役割です：{config.Sampling.VerifierRole}");
        }

        // 警告：網羅率を下げるもの（第18.3節「宣言がない場合の扱い」）
        var undeclared = keys.Keys.Where(k => !config.Authority.State.ContainsKey(k)).ToList();
        if (undeclared.Count > 0)
        {
            warnings.Add($"権威を宣言していない状態キーが {undeclared.Count} 個あります（defaults の最も厳しい扱いで検査します）：{string.Join(", ", undeclared)}");
        }

        if (config.Authority.Ownership.Count == 0)
        {
            warnings.Add("データの所有者（ownership）の宣言がありません。権威マップの網羅率が下がります");
        }

        return errors.Items;
    }
}
