# Blazor WASM 的发布产物是一堆静态文件（wwwroot），运行时不需要 .NET 运行时，
# 所以做成两段式：SDK 里 publish，nginx 里托管。
#
# 构建上下文是仓库根目录（AppHost 里 WithDockerfile 指定的 contextPath），
# 因为 Web 依赖 ShortLink.Contracts，单独拷 Web 目录编译不过。

FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src

# 先只拷工程文件，让 restore 的层缓存不被源码改动打破
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY src/ShortLink.Contracts/ShortLink.Contracts.csproj src/ShortLink.Contracts/packages.lock.json src/ShortLink.Contracts/
COPY src/ShortLink.Web/ShortLink.Web.csproj src/ShortLink.Web/packages.lock.json src/ShortLink.Web/
RUN dotnet restore src/ShortLink.Web/ShortLink.Web.csproj --locked-mode

COPY src/ShortLink.Contracts/ src/ShortLink.Contracts/
COPY src/ShortLink.Web/ src/ShortLink.Web/
RUN dotnet publish src/ShortLink.Web/ShortLink.Web.csproj -c Release -o /app/publish --no-restore

# 固定到实际构建验证过的官方 1.29.8 多架构清单，避免基础镜像静默漂移。
FROM nginx:1.29-alpine@sha256:5616878291a2eed594aee8db4dade5878cf7edcb475e59193904b198d9b830de AS runtime

# API 地址由容器启动时的 API_BASE_URL 注入，不在镜像里烧死部署域名。
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
COPY src/ShortLink.Web/appsettings.template.json /usr/share/nginx/html/appsettings.template.json
COPY src/ShortLink.Web/runtime-config.sh /docker-entrypoint.d/40-runtime-config.sh
RUN chmod 0755 /docker-entrypoint.d/40-runtime-config.sh
COPY src/ShortLink.Web/nginx.conf /etc/nginx/conf.d/default.conf

# Aspire 通过 HTTP_PORTS 告诉容器该监听哪个端口，nginx.conf 里与之对齐（8080）
EXPOSE 8080
