using System.Globalization;
using Custos.Core.Util;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Custos.Core.Config;

/// <summary>設定ファイルの書式の誤り。ファイル、行、列、キーの位置を持つ。</summary>
public sealed record ConfigError(string File, int Line, int Column, string Path, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{File}:{Line}:{Column}: {Path}: {Message}";
}

/// <summary>書式の誤りを集める。一つ目で止めず、すべてを報告する。</summary>
public sealed class ConfigErrors
{
    private readonly List<ConfigError> _items = [];

    /// <summary>集めた誤り。</summary>
    public IReadOnlyList<ConfigError> Items => _items;

    /// <summary>誤りがあるか。</summary>
    public bool Any => _items.Count > 0;

    /// <summary>誤りを足す。</summary>
    public void Add(string file, YamlNode? node, string path, string message)
    {
        var line = node == null ? 0 : (int)node.Start.Line;
        var column = node == null ? 0 : (int)node.Start.Column;
        _items.Add(new ConfigError(file, line, column, path, message));
    }

    /// <summary>位置のない誤りを足す（ファイルがない、キーの参照先がないなど）。</summary>
    public void Add(string file, string path, string message) => _items.Add(new ConfigError(file, 0, 0, path, message));

    /// <summary>別の誤りの集まりを足す。</summary>
    public void AddRange(IEnumerable<ConfigError> errors) => _items.AddRange(errors);
}

/// <summary>
/// YAML の値を、位置とキーの経路を覚えたまま読むための薄い包み。
/// 読んだキーを覚えておき、読まなかったキー（＝書式にないキー）を誤りとして報告する。
/// </summary>
public sealed class YNode
{
    internal YNode(YamlNode node, string file, string path, ConfigErrors errors)
    {
        Node = node;
        File = file;
        Path = path;
        Errors = errors;
    }

    /// <summary>元の YAML のノード。</summary>
    public YamlNode Node { get; }

    /// <summary>ファイル名。</summary>
    public string File { get; }

    /// <summary>キーの経路（例：state.player.hand.source）。</summary>
    public string Path { get; }

    /// <summary>誤りの集まり。</summary>
    public ConfigErrors Errors { get; }

    /// <summary>スカラーか。</summary>
    public bool IsScalar => Node is YamlScalarNode;

    /// <summary>マッピングか。</summary>
    public bool IsMap => Node is YamlMappingNode;

    /// <summary>列か。</summary>
    public bool IsList => Node is YamlSequenceNode;

    /// <summary>スカラーの値（null の表記は null）。</summary>
    public string? Scalar
    {
        get
        {
            if (Node is not YamlScalarNode scalar)
            {
                return null;
            }

            if (scalar.Style == YamlDotNet.Core.ScalarStyle.Plain && scalar.Value is "~" or "null" or "Null" or "NULL" or "")
            {
                return null;
            }

            return scalar.Value;
        }
    }

    /// <summary>null の表記か。</summary>
    public bool IsNull => Node is YamlScalarNode && Scalar == null;

    /// <summary>誤りを足す。</summary>
    public void Error(string message) => Errors.Add(File, Node, Path, message);

    /// <summary>マッピングとして読む。違えば誤りにして null。</summary>
    public YMap? AsMap()
    {
        if (Node is YamlMappingNode map)
        {
            return new YMap(map, this);
        }

        Error("マッピング（キーと値の組）を書いてください");
        return null;
    }

    /// <summary>列として読む。違えば誤りにして空。</summary>
    public IReadOnlyList<YNode> AsList()
    {
        if (Node is YamlSequenceNode seq)
        {
            return seq.Children.Select((child, i) => new YNode(child, File, $"{Path}[{i}]", Errors)).ToList();
        }

        Error("列（- で始まる並び）を書いてください");
        return [];
    }

    /// <summary>文字列として読む。</summary>
    public string? AsString()
    {
        if (Node is not YamlScalarNode)
        {
            Error("文字列を書いてください");
            return null;
        }

        return Scalar;
    }

    /// <summary>整数として読む。</summary>
    public long? AsInt(long? min = null, long? max = null)
    {
        var text = AsString();
        if (text == null)
        {
            return null;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            Error($"整数を書いてください（{text}）");
            return null;
        }

        if (min is { } lo && value < lo)
        {
            Error($"{lo} 以上にしてください（{value}）");
        }

        if (max is { } hi && value > hi)
        {
            Error($"{hi} 以下にしてください（{value}）");
        }

        return value;
    }

    /// <summary>数値として読む。</summary>
    public double? AsDouble(double? min = null, double? max = null)
    {
        var text = AsString();
        if (text == null)
        {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            Error($"数値を書いてください（{text}）");
            return null;
        }

        if (min is { } lo && value < lo)
        {
            Error($"{lo.ToString(CultureInfo.InvariantCulture)} 以上にしてください（{text}）");
        }

        if (max is { } hi && value > hi)
        {
            Error($"{hi.ToString(CultureInfo.InvariantCulture)} 以下にしてください（{text}）");
        }

        return value;
    }

    /// <summary>真偽値として読む。</summary>
    public bool? AsBool()
    {
        var text = AsString();
        switch (text)
        {
            case null:
                return null;
            case "true" or "True" or "TRUE" or "yes" or "on":
                return true;
            case "false" or "False" or "FALSE" or "no" or "off":
                return false;
            default:
                Error($"true か false を書いてください（{text}）");
                return null;
        }
    }

    /// <summary>決められた値のどれかとして読む。</summary>
    public string? AsEnum(params string[] allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        var text = AsString();
        if (text == null)
        {
            return null;
        }

        if (!allowed.Contains(text, StringComparer.Ordinal))
        {
            Error($"次のどれかを書いてください：{string.Join(" / ", allowed)}（{text}）");
            return null;
        }

        return text;
    }

    /// <summary>割合（"70%" または 0.7）として読む。0〜1 の小数で返す。</summary>
    public double? AsRatio()
    {
        var text = AsString();
        if (text == null)
        {
            return null;
        }

        if (!Units.TryParseRatio(text, out var ratio))
        {
            Error($"割合を書いてください（例：70%、0.1%、0.7）（{text}）");
            return null;
        }

        return ratio;
    }

    /// <summary>時間（"200ms"、"2s"、"1m"）として読む。</summary>
    public TimeSpan? AsDuration()
    {
        var text = AsString();
        if (text == null)
        {
            return null;
        }

        if (!Units.TryParseDuration(text, out var duration))
        {
            Error($"時間を書いてください（例：200ms、2s、1m）（{text}）");
            return null;
        }

        return duration;
    }

    /// <summary>大きさ（"4GB"、"512MB"）としてバイト数で読む。</summary>
    public long? AsBytes()
    {
        var text = AsString();
        if (text == null)
        {
            return null;
        }

        if (!Units.TryParseBytes(text, out var bytes))
        {
            Error($"大きさを書いてください（例：4GB、512MB）（{text}）");
            return null;
        }

        return bytes;
    }

    /// <summary>通信速度（"100Mbps"）としてビット毎秒で読む。</summary>
    public long? AsBitsPerSecond()
    {
        var text = AsString();
        if (text == null)
        {
            return null;
        }

        if (!Units.TryParseBitsPerSecond(text, out var bps))
        {
            Error($"通信速度を書いてください（例：100Mbps、20Mbps）（{text}）");
            return null;
        }

        return bps;
    }

    /// <summary>日付（"2026-11-20"）として読む。</summary>
    public DateOnly? AsDate()
    {
        var text = AsString();
        if (text == null)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Error($"日付を yyyy-MM-dd で書いてください（{text}）");
            return null;
        }

        return date;
    }

    /// <summary>文字列の列として読む。</summary>
    public IReadOnlyList<string> AsStringList()
    {
        var list = new List<string>();
        foreach (var item in AsList())
        {
            var text = item.AsString();
            if (text != null)
            {
                list.Add(text);
            }
            else if (item.IsNull)
            {
                item.Error("空の要素は書けません");
            }
        }

        return list;
    }
}

/// <summary>マッピングを読む。読んだキーを覚え、最後に Done() で書式にないキーを報告する。</summary>
public sealed class YMap
{
    private readonly YamlMappingNode _map;
    private readonly YNode _owner;
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    internal YMap(YamlMappingNode map, YNode owner)
    {
        _map = map;
        _owner = owner;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in map.Children.Keys)
        {
            if (key is YamlScalarNode scalar && scalar.Value is { } name && !seen.Add(name))
            {
                owner.Errors.Add(owner.File, key, Combine(name), "同じキーが二回書かれています");
            }
        }
    }

    /// <summary>このマッピングのノード。</summary>
    public YNode Owner => _owner;

    /// <summary>キーの一覧（書いた順）。</summary>
    public IEnumerable<string> Keys => _map.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? string.Empty);

    /// <summary>キーがあるか（null の値も「ある」とみなす）。</summary>
    public bool Has(string key) => _map.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value == key);

    /// <summary>値を取る。必須でなければ、ない場合は null。</summary>
    public YNode? Get(string key, bool required = false)
    {
        _consumed.Add(key);
        foreach (var pair in _map.Children)
        {
            if (pair.Key is YamlScalarNode scalar && scalar.Value == key)
            {
                return new YNode(pair.Value, _owner.File, Combine(key), _owner.Errors);
            }
        }

        if (required)
        {
            _owner.Errors.Add(_owner.File, _owner.Node, Combine(key), "必須の項目がありません");
        }

        return null;
    }

    /// <summary>すべての組を、読んだものとして列挙する（キーが自由な辞書用）。</summary>
    public IEnumerable<(string Key, YNode Value)> Entries()
    {
        foreach (var pair in _map.Children)
        {
            if (pair.Key is not YamlScalarNode scalar || scalar.Value == null)
            {
                _owner.Errors.Add(_owner.File, pair.Key, _owner.Path, "キーは文字列で書いてください");
                continue;
            }

            _consumed.Add(scalar.Value);
            yield return (scalar.Value, new YNode(pair.Value, _owner.File, Combine(scalar.Value), _owner.Errors));
        }
    }

    /// <summary>文字列。</summary>
    public string? Str(string key, bool required = false)
    {
        var node = Get(key, required);
        if (node == null || node.IsNull)
        {
            if (required && node != null)
            {
                node.Error("値が空です");
            }

            return null;
        }

        return node.AsString();
    }

    /// <summary>整数。</summary>
    public long? Whole(string key, bool required = false, long? min = null, long? max = null)
    {
        var node = Get(key, required);
        return node == null || (node.IsNull && !required) ? null : node.AsInt(min, max);
    }

    /// <summary>数値。</summary>
    public double? Number(string key, bool required = false, double? min = null, double? max = null)
    {
        var node = Get(key, required);
        return node == null || (node.IsNull && !required) ? null : node.AsDouble(min, max);
    }

    /// <summary>真偽値。</summary>
    public bool? Bool(string key, bool required = false)
    {
        var node = Get(key, required);
        return node == null || (node.IsNull && !required) ? null : node.AsBool();
    }

    /// <summary>決められた値のどれか。</summary>
    public string? Enum(string key, bool required, params string[] allowed)
    {
        var node = Get(key, required);
        return node == null || (node.IsNull && !required) ? null : node.AsEnum(allowed);
    }

    /// <summary>割合。</summary>
    public double? Ratio(string key, bool required = false)
    {
        var node = Get(key, required);
        return node == null || (node.IsNull && !required) ? null : node.AsRatio();
    }

    /// <summary>時間。</summary>
    public TimeSpan? Duration(string key, bool required = false)
    {
        var node = Get(key, required);
        return node == null || (node.IsNull && !required) ? null : node.AsDuration();
    }

    /// <summary>大きさ。</summary>
    public long? Bytes(string key, bool required = false)
    {
        var node = Get(key, required);
        return node == null || (node.IsNull && !required) ? null : node.AsBytes();
    }

    /// <summary>通信速度。</summary>
    public long? BitsPerSecond(string key, bool required = false)
    {
        var node = Get(key, required);
        return node == null || (node.IsNull && !required) ? null : node.AsBitsPerSecond();
    }

    /// <summary>日付。</summary>
    public DateOnly? Date(string key, bool required = false)
    {
        var node = Get(key, required);
        return node == null || (node.IsNull && !required) ? null : node.AsDate();
    }

    /// <summary>文字列の列。</summary>
    public IReadOnlyList<string> StrList(string key, bool required = false)
    {
        var node = Get(key, required);
        if (node == null || node.IsNull)
        {
            return [];
        }

        return node.AsStringList();
    }

    /// <summary>子のマッピング。</summary>
    public YMap? Map(string key, bool required = false)
    {
        var node = Get(key, required);
        if (node == null || (node.IsNull && !required))
        {
            return null;
        }

        return node.AsMap();
    }

    /// <summary>書式にないキーを誤りとして報告する。</summary>
    public void Done()
    {
        foreach (var pair in _map.Children)
        {
            if (pair.Key is YamlScalarNode scalar && scalar.Value != null && !_consumed.Contains(scalar.Value))
            {
                _owner.Errors.Add(_owner.File, pair.Key, Combine(scalar.Value), "書式にないキーです（綴りを確かめてください）");
            }
        }
    }

    private string Combine(string key) => string.IsNullOrEmpty(_owner.Path) ? key : $"{_owner.Path}.{key}";
}

/// <summary>YAML のファイルを読む。</summary>
public static class YamlDocument
{
    /// <summary>
    /// 文字列を読み、最上位のノードを返す。構文の誤りは errors に足し、null を返す。
    /// </summary>
    public static YNode? Parse(string text, string file, ConfigErrors errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
            if (stream.Documents.Count == 0)
            {
                errors.Add(file, string.Empty, "ファイルが空です");
                return null;
            }

            if (stream.Documents.Count > 1)
            {
                errors.Add(file, string.Empty, "一つのファイルには一つの文書だけを書いてください");
            }

            return new YNode(stream.Documents[0].RootNode, file, string.Empty, errors);
        }
        catch (YamlException e)
        {
            errors.AddRange([new ConfigError(file, (int)e.Start.Line, (int)e.Start.Column, string.Empty, "YAML の構文の誤り：" + e.Message)]);
            return null;
        }
    }
}

/// <summary>単位つきの値（割合、時間、大きさ、通信速度）を読む。</summary>
public static class Units
{
    /// <summary>"70%"、"0.1%"、"0.7" を 0〜1 の小数にする。</summary>
    public static bool TryParseRatio(string text, out double ratio)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        ratio = 0;
        if (text.EndsWith('%'))
        {
            if (!double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                return false;
            }

            ratio = percent / 100.0;
        }
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio))
        {
            return false;
        }

        return ratio >= 0 && !double.IsNaN(ratio) && !double.IsInfinity(ratio);
    }

    /// <summary>"200ms"、"2s"、"1.5m"、"1h" を時間にする。</summary>
    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        duration = TimeSpan.Zero;
        var (number, unit) = SplitUnit(text);
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return false;
        }

        double? ms = unit switch
        {
            "ms" => value,
            "s" => value * 1000,
            "m" or "min" => value * 60_000,
            "h" => value * 3_600_000,
            "d" => value * 86_400_000,
            _ => null,
        };
        if (ms == null)
        {
            return false;
        }

        duration = TimeSpan.FromMilliseconds(ms.Value);
        return true;
    }

    /// <summary>"4GB"、"512MB"、"1GiB" をバイト数にする。</summary>
    public static bool TryParseBytes(string text, out long bytes)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        bytes = 0;
        var (number, unit) = SplitUnit(text);
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return false;
        }

        double? multiplier = unit.ToUpperInvariant() switch
        {
            "B" or "" => 1,
            "KB" or "K" or "KIB" => 1024,
            "MB" or "M" or "MIB" => 1024 * 1024,
            "GB" or "G" or "GIB" => 1024L * 1024 * 1024,
            "TB" or "T" or "TIB" => 1024L * 1024 * 1024 * 1024,
            _ => null,
        };
        if (multiplier == null)
        {
            return false;
        }

        bytes = (long)(value * multiplier.Value);
        return true;
    }

    /// <summary>"100Mbps"、"20Mbps"、"1Gbps" をビット毎秒にする。</summary>
    public static bool TryParseBitsPerSecond(string text, out long bitsPerSecond)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        bitsPerSecond = 0;
        var (number, unit) = SplitUnit(text);
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return false;
        }

        double? multiplier = unit switch
        {
            "bps" => 1,
            "Kbps" or "kbps" => 1_000,
            "Mbps" or "mbps" => 1_000_000,
            "Gbps" or "gbps" => 1_000_000_000,
            _ => null,
        };
        if (multiplier == null)
        {
            return false;
        }

        bitsPerSecond = (long)(value * multiplier.Value);
        return true;
    }

    private static (string Number, string Unit) SplitUnit(string text)
    {
        var i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == '-' || text[i] == '+'))
        {
            i++;
        }

        return (text[..i], text[i..].Trim());
    }

    /// <summary>割合を表示用の文字列にする（0.001 → 0.1%）。</summary>
    public static string FormatPercent(double ratio) => (ratio * 100).ToString("0.####", CultureInfo.InvariantCulture) + "%";

    /// <summary>失敗時の例外。</summary>
    public static CustosException Invalid(string what, string text) => new($"{what} を読めません: {text}");
}
