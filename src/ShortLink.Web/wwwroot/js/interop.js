// Blazor 与浏览器之间的小工具集。
// 说明：Blazor WASM 里 byte[]  marshalling 为 Uint8Array（不经过 base64），
// 因此大文件（CSV 导出可达十万行）也能一次性交给浏览器，不需要服务端中转。
window.shortlink = {
    saveFile: function (fileName, contentType, bytes) {
        try {
            const blob = new Blob([bytes], { type: contentType });
            const url = URL.createObjectURL(blob);
            const anchor = document.createElement('a');
            anchor.href = url;
            anchor.download = fileName;
            anchor.rel = 'noopener';
            document.body.appendChild(anchor);
            anchor.click();
            anchor.remove();
            // 立即 revoke 会让部分浏览器中断下载，延迟释放。
            setTimeout(function () { URL.revokeObjectURL(url); }, 4000);
            return true;
        } catch (e) {
            console.error('saveFile failed', e);
            return false;
        }
    }
};
