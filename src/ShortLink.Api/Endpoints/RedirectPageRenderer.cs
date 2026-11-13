using System.Net;

namespace ShortLink.Api.Endpoints;

/// <summary>跳转链路的 HTML 落地页（错误页与密码页），内联样式避免依赖静态资源。</summary>
public static class RedirectPageRenderer
{
    public static string ErrorPage(string title, string message)
        => Layout($$"""
            <div class="card">
              <div class="badge">ShortLink</div>
              <h1>{{Encode(title)}}</h1>
              <p>{{Encode(message)}}</p>
              <p class="hint">如果你认为这是误判，请联系链接的创建者。</p>
            </div>
            """);

    public static string PasswordForm(string code, string? error)
        => Layout($$"""
            <div class="card">
              <div class="badge">ShortLink</div>
              <h1>该链接需要访问密码</h1>
              <p class="hint">请输入创建者提供的密码后继续访问。</p>
              {{(string.IsNullOrEmpty(error) ? string.Empty : $"<p class=\"error\">{Encode(error)}</p>")}}
              <form method="post" action="/{{Encode(code)}}">
                <input type="password" name="password" placeholder="访问密码" autocomplete="off" required autofocus />
                <button type="submit">继续访问</button>
              </form>
            </div>
            """);

    /// <summary>
    /// 微信 / QQ 内打开的中间页。这两个内置浏览器会拦截未知域名，
    /// 先引导用户「在浏览器中打开」，比直接 302 更不容易被判定为风险链接。
    /// </summary>
    public static string Interstitial(string code, string targetUrl, string? title)
        => Layout($$"""
            <div class="card">
              <div class="badge">ShortLink</div>
              <h1>{{Encode(string.IsNullOrWhiteSpace(title) ? "即将跳转到外部页面" : title!)}}</h1>
              <p class="hint">当前在微信 / QQ 内置浏览器中打开，如无法跳转，请点击右上角「···」选择「在浏览器打开」。</p>
              <p class="mono">{{Encode(Shorten(targetUrl))}}</p>
              <form method="get" action="/{{Encode(code)}}">
                <input type="hidden" name="direct" value="1" />
                <button type="submit">仍要继续访问</button>
              </form>
              <p class="hint"><a href="{{Encode(targetUrl)}}" rel="noopener noreferrer nofollow">直接打开目标地址</a></p>
            </div>
            """);

    /// <summary>安全拦截页：不透露具体命中规则，避免被用来试探策略边界。</summary>
    public static string BlockedPage(string reason)
        => Layout($$"""
            <div class="card">
              <div class="badge">ShortLink</div>
              <h1>访问受限</h1>
              <p>{{Encode(reason)}}</p>
              <p class="hint">如果这是误判，请联系链接创建者或站点管理员。</p>
            </div>
            """);

    private static string Shorten(string url)
        => url.Length <= 64 ? url : string.Concat(url.AsSpan(0, 61), "...");

    private static string Layout(string body)
        => $$"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <meta name="robots" content="noindex" />
              <title>ShortLink</title>
              <style>
                :root { color-scheme: light; }
                * { box-sizing: border-box; }
                body {
                  margin: 0; min-height: 100vh; display: grid; place-items: center;
                  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
                  background: linear-gradient(160deg, #fff5f5 0%, #ffe8e8 45%, #ffd9d9 100%);
                  color: #2b1214; padding: 24px;
                }
                .card {
                  width: 100%; max-width: 420px; background: #fff; border-radius: 18px;
                  padding: 36px 32px; box-shadow: 0 18px 48px rgba(140, 6, 25, 0.16);
                  border-top: 4px solid #d70b2a; text-align: center;
                }
                .badge {
                  display: inline-block; font-size: 12px; letter-spacing: .16em; text-transform: uppercase;
                  color: #d70b2a; background: #ffecee; border-radius: 999px; padding: 4px 12px; margin-bottom: 18px;
                }
                h1 { font-size: 22px; margin: 0 0 12px; color: #8c0619; }
                p { margin: 0 0 10px; line-height: 1.7; color: #4a2b2f; }
                .hint { font-size: 14px; color: #7c5a5f; }
                .mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 13px; word-break: break-all; color: #8c0619; }
                a { color: #d70b2a; }
                .error { font-size: 14px; color: #d70b2a; background: #fff1f2; border-radius: 10px; padding: 8px 12px; }
                form { display: flex; gap: 10px; margin-top: 22px; }
                input {
                  flex: 1; padding: 12px 14px; border-radius: 10px; border: 1px solid #e6c9cd;
                  font-size: 15px; outline: none;
                }
                input:focus { border-color: #d70b2a; box-shadow: 0 0 0 3px rgba(215, 11, 42, .12); }
                button {
                  padding: 12px 18px; border: 0; border-radius: 10px; cursor: pointer; font-size: 15px;
                  color: #fff; background: linear-gradient(135deg, #d70b2a, #8c0619);
                }
                button:hover { filter: brightness(1.06); }
              </style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
