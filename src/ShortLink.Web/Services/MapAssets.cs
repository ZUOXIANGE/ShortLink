using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;

namespace ShortLink.Web.Services;

/// <summary>地图中的一个区域。<c>Id</c> 对世界图是 ISO2，对中国图是行政区划码。</summary>
public sealed record MapLocation(string Id, string Name, string Path);

/// <summary>预生成的地图 path 数据（不依赖运行时图表库）。</summary>
public sealed record MapDefinition(string Label, string ViewBox, IReadOnlyList<MapLocation> Locations);

/// <summary>
/// 加载 wwwroot/maps 下的地图数据。
/// <para>
/// 不能复用注入的 <see cref="HttpClient"/>：它的 BaseAddress 指向 API（5080），
/// 用它取同源静态文件会打到 API 上。这里按 <see cref="NavigationManager.BaseUri"/> 单独建一个。
/// </para>
/// </summary>
public sealed class MapAssets : IDisposable
{
    private readonly HttpClient _http;
    private Task<MapDefinition>? _world;
    private Task<MapDefinition>? _china;

    public MapAssets(NavigationManager navigation)
    {
        _http = new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };
    }

    public Task<MapDefinition> GetWorldAsync() => _world ??= LoadAsync("maps/world.json");

    public Task<MapDefinition> GetChinaAsync() => _china ??= LoadAsync("maps/china.json");

    private async Task<MapDefinition> LoadAsync(string path)
    {
        var map = await _http.GetFromJsonAsync<MapDefinition>(path);
        return map ?? throw new InvalidOperationException($"地图数据缺失：{path}");
    }

    public void Dispose() => _http.Dispose();
}
