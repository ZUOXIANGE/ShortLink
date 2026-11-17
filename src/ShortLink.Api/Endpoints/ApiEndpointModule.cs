using ServiceScan.SourceGenerator;

namespace ShortLink.Api.Endpoints;

/// <summary>
/// 挂在 <c>/api/v{version}</c> 版本组下的端点模块。
/// <para>
/// 实现类型只要提供签名为 <c>static void MapEndpoints(RouteGroupBuilder)</c> 的方法即可被
/// 源生成器发现，无需在 <c>Program.cs</c> 里逐个手工登记。生成的是显式调用，不是运行期反射扫描，
/// 因此启动开销与手写等价，且裁剪/AOT 下同样有效。
/// </para>
/// <para>
/// 只覆盖 <c>api</c> 组：健康探针、短码跳转与第三方开放接口各有独立的认证与路由位置，
/// 仍由 <c>Program.cs</c> 显式注册。
/// </para>
/// </summary>
public interface IApiEndpointModule
{
    static abstract void MapEndpoints(RouteGroupBuilder group);
}

/// <summary>端点模块的注册入口：新增模块时只需实现 <see cref="IApiEndpointModule"/>，这里不用改。</summary>
public static partial class ApiEndpointRegistration
{
    [ScanForTypes(
        AssignableTo = typeof(IApiEndpointModule),
        Handler = nameof(IApiEndpointModule.MapEndpoints))]
    public static partial RouteGroupBuilder MapApiEndpoints(this RouteGroupBuilder group);
}
