using Microsoft.JSInterop;

namespace ShortLink.Web.Services;

/// <summary>
/// 浏览器端文件保存。CSV 导出走 <c>byte[]</c> → JS <c>Uint8Array</c>，
/// 由浏览器直接落盘，不经过服务端二次转发。
/// </summary>
public sealed class FileSaver(IJSRuntime js)
{
    public async Task<bool> SaveAsync(string fileName, string contentType, byte[] content)
    {
        try
        {
            return await js.InvokeAsync<bool>("shortlink.saveFile", fileName, contentType, content);
        }
        catch (JSException)
        {
            return false;
        }
    }
}
