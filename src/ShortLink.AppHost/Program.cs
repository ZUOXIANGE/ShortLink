using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Microsoft.Extensions.Configuration;
using Projects;

// ─────────────────────────────────────────────────────────────────────────────
// ShortLink 本地编排入口。
//
// 一条命令拉起「依赖 + 服务」：
//     dotnet run --project src/ShortLink.AppHost
//
// 三条刻意的设计约束：
//
// 1. AppHost 只做「资源 → 环境变量」的翻译，不往应用里塞 Aspire 客户端包。
//    应用侧照旧只认 IConfiguration（Database__* / Cache__* / Messaging__*），
//    所以同一份代码不走 Aspire 也能用 appsettings、compose 或 k8s 起起来。
//
// 2. API 与 Web 的对外端口写死（5080 / 5081）。
//    管理后台是 Blazor WASM，容器启动时由 entrypoint 把 API_BASE_URL 写入运行时配置；
//    CORS 白名单与短链默认域名也由下方公网参数显式注入，避免浏览器误连 localhost。
//    依赖容器（MySQL / Redis / Kafka）则用随机端口，避免和本机已装的服务抢 3306 / 6379。
//
// 3. 不引入 ServiceDefaults 项目。
//    Api 自己已经注册了 OpenTelemetry 三信号与 /health（含 /health/ready 的真实语义），
//    再套一层 AddServiceDefaults 会出现两套 meter / 健康检查互相覆盖。
//    这里只把仪表盘的 OTLP 端点通过 OpenTelemetry__Endpoint 递进去。
// ─────────────────────────────────────────────────────────────────────────────

var builder = DistributedApplication.CreateBuilder(args);

// 本地公网参数与下面两个固定端口保持一致；发布产物由部署方显式覆盖。
const int ApiPort = 5080;
const int WebPort = 5081;

const string MySqlDevPassword = "ChangeMe_123!";

// publish 模式下参数不能带开发默认值：生成物里的口令/密钥必须是「运维填」的占位符，
// 把 dev 口令写进 compose/k8s 是最容易被复制到生产的一类事故。
var isPublishMode = builder.ExecutionContext.IsPublishMode;

// 开关都走 configuration：appsettings.json 给默认值，命令行可覆盖，
// 例如 `dotnet run --project src/ShortLink.AppHost --ShortLink:Database=sqlite`。
var useMySql = !string.Equals(
    builder.Configuration["ShortLink:Database"], "sqlite", StringComparison.OrdinalIgnoreCase);
var useRedis = ReadBool(builder.Configuration, "ShortLink:UseRedis", fallback: true);
var useKafka = ReadBool(builder.Configuration, "ShortLink:UseKafka", fallback: false);
var mySqlImageTag = builder.Configuration["ShortLink:MySqlImageTag"] ?? "8.4";
var mySqlServerVersion = builder.Configuration["ShortLink:MySqlVersion"] ?? "8.4.0";

// 浏览器实际访问的地址，不是容器网络里的服务名；publish 时必须由部署方显式填写。
var webPublicOrigin = isPublishMode
    ? builder.AddParameter("web-public-origin")
    : builder.AddParameter("web-public-origin", $"http://localhost:{WebPort}");
var apiPublicBaseUrl = isPublishMode
    ? builder.AddParameter("api-public-base-url")
    : builder.AddParameter("api-public-base-url", $"http://localhost:{ApiPort}");

var api = builder.AddProject<Projects.ShortLink_Api>("api")
    .WithHttpEndpoint(port: ApiPort, name: "http")
    // 跳转入口必须能从宿主机访问：短链形如 http://<domain>/abc123，暴露不出去等于平台不可用。
    .WithExternalHttpEndpoints()
    // 就绪探针检查 DB 与已配置的缓存；Web 只有在 API 可接流量后才启动。
    .WithHttpHealthCheck("/health/ready");

// ── 密钥类配置：本地给开发默认值，publish 时留空由运维填 ──────────
// 留空不是疏忽：Api 的 JwtOptions 会在启动期校验密钥长度，占了位却没填会直接起不来，
// 这比「带着一个弱密钥上线、谁也看不出来」安全得多。
var jwtSigningKey = isPublishMode
    ? builder.AddParameter("jwt-signing-key", secret: true)
    : builder.AddParameter("jwt-signing-key", "shortlink-dev-only-signing-key-32bytes-min", secret: true);

var adminPassword = isPublishMode
    ? builder.AddParameter("seed-admin-password", secret: true)
    : builder.AddParameter("seed-admin-password", "Admin@123456", secret: true);

api.WithEnvironment("Jwt__SigningKey", jwtSigningKey)
    .WithEnvironment("Seed__AdminPassword", adminPassword)
    .WithEnvironment("Cors__AllowedOrigins__0", webPublicOrigin);

// 短链对外域名：本机就是 localhost:5080；部署到服务器必须换成真实域名，
// 否则建出来的短链全部指向访问者自己的 localhost（这类事故排查成本极高，所以放进 .env 让运维显式填）。
var defaultDomainHost = isPublishMode
    ? builder.AddParameter("default-domain-host")
    : builder.AddParameter("default-domain-host", $"localhost:{ApiPort}");
var defaultDomainScheme = isPublishMode
    ? builder.AddParameter("default-domain-scheme")
    : builder.AddParameter("default-domain-scheme", "http");

api.WithEnvironment("Seed__DefaultDomainHost", defaultDomainHost)
    .WithEnvironment("Seed__DefaultDomainScheme", defaultDomainScheme);

if (useMySql)
{
    // 口令只用于本地开发容器，生产走 Secret；不要往这里抄线上口令。
    // publish 时刻意不给默认值：生成的 .env / Helm values 里留空，由运维填。
    var mysqlPassword = isPublishMode
        ? builder.AddParameter("mysql-password", secret: true)
        : builder.AddParameter("mysql-password", MySqlDevPassword, secret: true);

    var mysql = builder.AddMySql("mysql", mysqlPassword)
        .WithImageTag(mySqlImageTag)
        .WithDataVolume("shortlink-mysql-data");

    // 连接串按项目既有格式显式拼装，而不是直接用 AddMySql 生成的：
    // 项目依赖 AllowUserVariables / UseAffectedRows / CharSet 这几个参数
    //（批量更新与 utf8mb4 行为），丢掉它们会在运行期才暴露成中文乱码或参数化失败。
    // 也不用容器服务名当主机名——应用进程跑在宿主机上，只能走映射出来的 host:port。
    var endpoint = mysql.GetEndpoint("tcp");
    var connectionString = ReferenceExpression.Create(
        $"Server={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database=shortlink;User Id=root;Password={mysqlPassword.Resource};CharSet=utf8mb4;AllowUserVariables=true;UseAffectedRows=false;");

    api.WaitFor(mysql)
        .WithEnvironment("Database__Provider", "MySql")
        // 与镜像标签成对维护：ServerVersion 决定 EF 的方言分支，不匹配时个别特性会被错误降级。
        .WithEnvironment("Database__MySqlVersion", mySqlServerVersion)
        .WithEnvironment("Database__ConnectionString", connectionString);
}
else
{
    // 零容器兜底路径：只想快速验证前端/接口时用，不覆盖迁移脚本的真实行为。
    api.WithEnvironment("Database__Provider", "Sqlite")
        .WithEnvironment("Database__ConnectionString", "Data Source=shortlink-aspire.db");
}

if (useRedis)
{
    var redis = builder.AddRedis("redis");

    // 配了 Redis 才启用分布式缓存 + FusionCache 背板 + 按 IP 分区的分布式限流；
    // 留空则回落到进程内缓存与本地窗口（单实例可用，多实例会静默不失效）。
    //
    // 这里用资源自带的连接串表达式，而不是自己拼 host:port：
    // Aspire 的 Redis 容器默认开启 requirepass（口令随机生成），
    // 少拼 `password=` 的症状是启动日志一切正常、只有 /health/ready 的 cache 一直 fail。
    api.WaitFor(redis)
        .WithEnvironment("Cache__RedisConnectionString", redis.Resource.ConnectionStringExpression);
}

if (useKafka)
{
    var kafka = builder.AddKafka("kafka");

    // MessagingOptions 在启动期会校验：Transport=kafka 却没填 broker 地址直接起不来，
    // 这两个键必须成对出现。
    api.WaitFor(kafka)
        .WithEnvironment("Messaging__Transport", "kafka")
        .WithEnvironment("Messaging__Kafka__BootstrapServers",
            ReferenceExpression.Create($"{kafka.GetEndpoint("tcp").Property(EndpointProperty.HostAndPort)}"));
}

// 仪表盘的 OTLP/gRPC 端点由 launchSettings 的 ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL 固定下来；
// 没设时不要瞎猜端口，宁可让链路追踪留空，也不要让 Api 每次导出都报连接失败。
//
// publish 模式下刻意不传：那个时候容器里没有 localhost:19000，硬塞等于让导出一直失败。
// 发布产物里由 Aspire 注入标准的 OTEL_EXPORTER_OTLP_ENDPOINT，Api 侧按标准变量兜底。
var otlpEndpoint = builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"];
if (!isPublishMode && !string.IsNullOrWhiteSpace(otlpEndpoint))
{
    api.WithEnvironment("OpenTelemetry__Endpoint", otlpEndpoint);
}

// 管理后台：纯静态 WASM，只需等 API 起来（种子账号与默认域名由 API 启动时创建）。
builder.AddProject<Projects.ShortLink_Web>("web")
    .WithHttpEndpoint(port: WebPort, name: "http")
    .WithExternalHttpEndpoints()
    .WithEnvironment("API_BASE_URL", apiPublicBaseUrl)
    // 默认的托管方式是 DevServer——那是开发服务器，会被当成「生产镜像」发出去。
    // 发布时改用 nginx 托管静态文件（构建上下文是仓库根，因为 Web 依赖 Contracts）。
    .PublishAsDockerFile(container => container.WithDockerfile(
        contextPath: "../..",
        dockerfilePath: "src/ShortLink.Web/Dockerfile",
        stage: "runtime"))
    .WaitFor(api);

// ── 发布端（compose / Helm）──────────────────────────────────────────────
// 只在 publish 模式挂环境资源：run 模式它不参与编排，但模型里同时存在两个
// compute environment 时管线会拒绝发布（validate-compute-environments 要求
// 逐个资源 WithComputeEnvironment 绑定），所以一次只挂一个目标。
//
// 目标优先级：ShortLink:PublishTarget（环境变量 ShortLink__PublishTarget）> --publisher > compose。
// 之所以不只看 --publisher：Aspire CLI 的 `aspire publish` 没有 --publisher 参数，
// 它按模型里的 compute environment 决定目标，配置开关才是 CLI 与 dotnet run 两条路都能用的入口。
if (isPublishMode)
{
    var target = builder.Configuration["ShortLink:PublishTarget"]
                 ?? builder.ExecutionContext.PublisherName
                 ?? "docker-compose";

    if (string.Equals(target, "kubernetes", StringComparison.OrdinalIgnoreCase))
    {
        builder.AddKubernetesEnvironment("shortlink");
    }
    else
    {
        // 仪表盘默认不进产物：它没有认证，谁能访问端口谁就能看 trace（含完整 URL 与 SQL 片段）。
        // 需要排障时用 ShortLink__PublishDashboard=true 显式打开。
        AddComposeEnvironment();
    }
}

void AddComposeEnvironment()
{
    var dashboardEnabled = ReadBool(builder.Configuration, "ShortLink:PublishDashboard", fallback: false);

    builder.AddDockerComposeEnvironment("shortlink")
        .WithDashboard(dashboardEnabled)
        .ConfigureComposeFile(file =>
        {
            // MySQL 8.4 初始化要十几秒，而 compose 默认只等「容器启动」。
            // Api 启动即跑迁移，抢在初始化完成之前就会失败退出（且 compose 不会自动重试），
            // 所以给 mysql 加健康检查，再把 api 的依赖条件提到 service_healthy。
            if (file.Services.TryGetValue("mysql", out var mysql))
            {
                mysql.Healthcheck = new Healthcheck
                {
                    Test = ["CMD-SHELL", "mysqladmin ping -h 127.0.0.1 -uroot -p$$MYSQL_ROOT_PASSWORD --silent"],
                    Interval = "10s",
                    Timeout = "5s",
                    Retries = 12,
                    StartPeriod = "30s",
                };
            }

            if (file.Services.TryGetValue("api", out var apiService))
            {
                // 不使用框架的“信任所有上游”快捷开关；需要反代时由部署方显式配置可信代理。
                apiService.Environment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "false";
                apiService.Environment["ReverseProxy__TrustForwardedHeaders"] = "false";

                if (apiService.DependsOn.TryGetValue("mysql", out var mysqlDependency))
                {
                    mysqlDependency.Condition = "service_healthy";
                }

                // 默认 aspnet 镜像没有 curl/wget，但有 bash；用 /dev/tcp 发起真实 HTTP 就绪检查。
                // 就绪而非仅进程存活：DB 或已配置的 Redis 不可用时，Web 不应先于 API 接流量。
                apiService.Healthcheck = new Healthcheck
                {
                    Test = ["CMD-SHELL", "bash -c 'exec 3<>/dev/tcp/127.0.0.1/$${API_PORT:-8080} && printf \"GET /health/ready HTTP/1.0\\r\\n\\r\\n\" >&3 && IFS= read -r status <&3 && [[ \"$$status\" == *\" 200 \"* ]]'"],
                    Interval = "10s",
                    Timeout = "5s",
                    Retries = 12,
                    StartPeriod = "20s",
                };
            }

            if (file.Services.TryGetValue("web", out var webService))
            {
                webService.Environment["ASPNETCORE_FORWARDEDHEADERS_ENABLED"] = "false";
                if (webService.DependsOn.TryGetValue("api", out var apiDependency))
                {
                    apiDependency.Condition = "service_healthy";
                }
            }

            // 同时指定 host 端口 + 外部端点时，compose 会生成两条映射：
            // "5080:8080" 和一条没有主机端口的 "8080"（等于再随机映射一次，纯噪音）。
            NormalizePorts(file, "api");
            NormalizePorts(file, "web");
        });
}

// 只保留「主机端口:容器端口」形式的映射，丢掉裸容器端口那一条。
static void NormalizePorts(Aspire.Hosting.Docker.Resources.ComposeFile file, string serviceName)
{
    if (file.Services.TryGetValue(serviceName, out var service) && service.Ports is { } ports)
    {
        service.Ports = ports.Where(port => port.Contains(':')).ToList();
    }
}

builder.Build().Run();

// AppHost 不引 Microsoft.Extensions.Configuration.Binder，布尔开关自己解析：
// 只为两个开关多带一个包不值得，配置少一个间接依赖就少一处版本要对齐。
static bool ReadBool(IConfiguration configuration, string key, bool fallback) =>
    bool.TryParse(configuration[key], out var value) ? value : fallback;
