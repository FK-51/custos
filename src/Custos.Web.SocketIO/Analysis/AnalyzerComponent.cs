using System.Text.Json.Nodes;
using Custos.Core.Util;
using Custos.Web.SocketIO.Docker;

namespace Custos.Web.SocketIO.Analysis;

/// <summary>
/// 解析の外部コンポーネント（設計書 第12.8節）。Python のコンテナ（custos-analyzer-image）を、決まった形の入力（画像とパラメータ）と
/// 出力（JSON）だけでやり取りして使う。管理側だけで動かし、ネットワークにはつながない（--network none）。
/// </summary>
public sealed class AnalyzerComponent
{
    /// <summary>イメージの名前。</summary>
    public const string Image = "custos-analyzer-image";

    private readonly DockerCli _docker;
    private readonly Workspace _workspace;

    /// <summary>作る。</summary>
    public AnalyzerComponent(DockerCli docker, Workspace workspace)
    {
        _docker = docker;
        _workspace = workspace;
    }

    /// <summary>イメージがあるか（使っているか）。</summary>
    public async Task<bool> AvailableAsync(CancellationToken cancellationToken)
    {
        var tag = await TagAsync(cancellationToken).ConfigureAwait(false);
        return tag != null;
    }

    private async Task<string?> TagAsync(CancellationToken cancellationToken)
    {
        var result = await _docker.RunAsync(["image", "ls", Image, "--format", "{{.Repository}}:{{.Tag}}"], TimeSpan.FromSeconds(20), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    /// <summary>画像を解析する。dir は入力（画像と params.json）のディレクトリ（データの置き場所の中）。</summary>
    public async Task<JsonObject> AnalyzeAsync(string dir, CancellationToken cancellationToken)
    {
        var tag = await TagAsync(cancellationToken).ConfigureAwait(false) ?? throw new CustosException($"{Image} のイメージがありません（analyzers/image を作ってください）");
        var result = await _docker.RunAsync(["run", "--rm", "--network", "none", "-v", dir + ":/data:ro", tag, "analyze", "/data/params.json"], TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("画像の解析");
        return JsonNode.Parse(result.StandardOutput) as JsonObject ?? throw new CustosException("解析の結果を読めません");
    }

    /// <summary>既知の見本（崩れた画像と正常な画像）で、検出することと誤検出しないことを確かめる。</summary>
    public async Task<JsonObject> SelfCheckAsync(CancellationToken cancellationToken)
    {
        var tag = await TagAsync(cancellationToken).ConfigureAwait(false) ?? throw new CustosException($"{Image} のイメージがありません");
        var result = await _docker.RunAsync(["run", "--rm", "--network", "none", tag, "selfcheck"], TimeSpan.FromMinutes(5), cancellationToken: cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("解析の外部コンポーネントの自己確認");
        var node = JsonNode.Parse(result.StandardOutput) as JsonObject ?? throw new CustosException("自己確認の結果を読めません");
        node["image"] = tag;
        _ = _workspace;
        return node;
    }
}
