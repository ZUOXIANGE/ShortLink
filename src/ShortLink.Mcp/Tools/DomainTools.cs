using System.ComponentModel;
using ModelContextProtocol.Server;
using ShortLink.Application.Domains;
using ShortLink.Domain.Users;
using ShortLink.Mcp.Models;

namespace ShortLink.Mcp.Tools;

/// <summary>
/// 域名只读工具。
/// <para>
/// 只做「列出来」：域名的增删改会影响所有已分发短链的解析，
/// 不适合由 AI 在一次对话里顺手完成，因此不提供写工具。
/// </para>
/// </summary>
[McpServerToolType]
public sealed class DomainTools(DomainService domains, McpToolRunner runner)
{
    [McpServerTool(Name = "shortlink_list_domains", ReadOnly = true)]
    [Description(
        "列出可用的短链域名（含 ID、主机名、协议、是否默认、该域名下的短链数）。只读操作。" +
        "创建短链需要指定 domainId 时先调本工具取值；不指定则用默认域名。")]
    public Task<IReadOnlyList<DomainOption>> ListAsync(CancellationToken cancellationToken = default)
        => runner.RunAsync(
            "shortlink_list_domains",
            UserRole.Viewer,
            async ct =>
            {
                var list = await domains.ListAsync(ct).ConfigureAwait(false);

                return (IReadOnlyList<DomainOption>)list
                    .Select(d => new DomainOption(d.Id, d.Host, d.Scheme, d.IsDefault, d.IsEnabled, d.LinkCount))
                    .ToList();
            },
            cancellationToken);
}
