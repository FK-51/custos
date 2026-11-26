using System.Globalization;
using Custos.Core.Util;

namespace Custos.Cli;

/// <summary>コマンドの引数（--名前 値、--名前（値なし）、位置引数）。</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    /// <summary>引数を読む。値をとらない名前は flags に入れる。</summary>
    public CliArgs(IEnumerable<string> args, IReadOnlySet<string> flags)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(flags);
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var name = arg[2..];
                string value;
                var eq = name.IndexOf('=', StringComparison.Ordinal);
                if (eq >= 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                else if (flags.Contains(name))
                {
                    value = "true";
                }
                else if (i + 1 < list.Count)
                {
                    value = list[++i];
                }
                else
                {
                    throw new CustosException($"--{name} に値がありません");
                }

                if (!_options.TryGetValue(name, out var values))
                {
                    values = [];
                    _options[name] = values;
                }

                values.Add(value);
            }
            else if (arg == "-m" && i + 1 < list.Count)
            {
                Add("message", list[++i]);
            }
            else
            {
                Positional.Add(arg);
            }
        }
    }

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            values = [];
            _options[name] = values;
        }

        values.Add(value);
    }

    /// <summary>位置引数。</summary>
    public List<string> Positional { get; } = [];

    /// <summary>値（最後に指定したもの）。</summary>
    public string? Get(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var values) ? values[^1] : null;
    }

    /// <summary>すべての値。</summary>
    public IReadOnlyList<string> All(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var values) ? values : [];
    }

    /// <summary>真偽の指定。</summary>
    public bool Flag(string name) => Get(name) is "true" or "1" or "yes";

    /// <summary>整数の値。</summary>
    public long Number(string name, long fallback)
    {
        var text = Get(name);
        return text == null ? fallback : long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    /// <summary>位置引数（必須）。</summary>
    public string Arg(int index, string what) =>
        index < Positional.Count ? Positional[index] : throw new CustosException($"{what} を指定してください");

    /// <summary>使われなかった名前（綴りの誤り）を返す。</summary>
    public IEnumerable<string> Unused() => _options.Keys.Where(k => !_used.Contains(k));
}
