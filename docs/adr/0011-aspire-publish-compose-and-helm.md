# ADR 0011：发布端产出（Docker Compose / Helm）

- 状态：已采纳
- 日期：2026-11-12
- 相关：`src/ShortLink.AppHost/Program.cs`、`src/ShortLink.Web/Dockerfile`、`src/ShortLink.Web/nginx.conf`、`.config/dotnet-tools.json`、`docs/adr/0010-aspire-apphost-orchestration.md`

## 背景

本地编排（ADR 0010）解决的是「一条命令起得来」。但要把同一份资源图交给运维，还需要**可交付的部署文件**：一个 `docker-compose.yaml`，或者一个 Helm chart。

手写这两套文件的问题是它们与 AppHost 里的资源定义会分叉：依赖版本、环境变量名、密码注入方式各写一遍，改一处漏一处，最后「本地能跑、部署跑不起来」。

Aspire 13 提供了发布端（publisher）：把同一份资源图渲染成目标格式，避免二套定义。

## 决策

### 1. 用 Aspire CLI（本地工具）而不是自研脚本

`.config/dotnet-tools.json` 里新增 `aspire.cli`，与既有的 `dotnet-ef` 一样走 `dotnet tool restore`：

```bash
dotnet aspire publish -o artifacts/compose                      # compose + .env
ShortLink__PublishTarget=kubernetes dotnet aspire publish -o artifacts/k8s   # Helm chart
```

- 不引入全局安装（`dotnet tool install -g`），团队靠 `dotnet tool restore` 就能对齐版本。
- `dotnet run --project src/ShortLink.AppHost -- --operation publish --publisher docker-compose --output-path ...` 这条老路径仍可用，但它会在结束阶段跑一次 `docker-compose down` 并因为「产物还没部署过」而报错退出；CLI 的管线筛选是正确的，所以以 CLI 为准。

### 2. 目标环境二选一，由配置决定，不靠 `--publisher`

模型里同时存在两个 compute environment 时，管线会直接拒绝（`validate-compute-environments`）。而 Aspire CLI 的 `aspire publish` 没有 `--publisher` 参数，所以目标选择必须落到配置：

```csharp
var target = builder.Configuration["ShortLink:PublishTarget"]      // ShortLink__PublishTarget
             ?? builder.ExecutionContext.PublisherName            // dotnet run --publisher 兼容
             ?? "docker-compose";
```

默认 compose。 Kubernetes 集成目前是 **preview** 包（`Aspire.Hosting.Kubernetes` 13.6.5-preview.1.26559.7），升级时跟同一批 preview 版本走。

### 3. 发布阶段不写死任何密钥

`publish 模式`下所有敏感参数**不给默认值**：

| 参数 | 用途 | 为什么必须留空 |
| --- | --- | --- |
| `jwt-signing-key` | `Jwt:SigningKey` | 弱密钥照样能签发 token，线上不会报错，只会一直不安全 |
| `seed-admin-password` | 种子管理员口令 | 同上，默认值会跟着产物一起被复制 |
| `mysql-password` | MySQL root 口令 | 容器口令进了 git 就是既成事实 |
| `default-domain-host` | `Seed:DefaultDomainHost` | 留空/默认 `localhost:5080` 会让建出来的短链全指向访问者自己的机器 |

`docker-compose.yaml` 里引用 `${JWT_SIGNING_KEY}` 这类占位符，`.env` 里留空由运维填。留空不是疏忽：`JwtOptions` 启动期校验密钥长度，占了位没填会直接起不来，比「带着弱密钥上线」安全。

### 4. Blazor WASM 的发布镜像换成 nginx 托管

默认把 `ShortLink.Web` 发布成容器，跑的是 **DevServer**——那是开发服务器，不适合当生产镜像。所以 `PublishAsDockerFile` 指向自带的 `src/ShortLink.Web/Dockerfile`（两段式：SDK 里 `dotnet publish`，nginx 里托管静态文件），配套 `nginx.conf` 做 SPA 回落与框架文件 `no-cache`。

构建上下文是仓库根目录（Web 依赖 `ShortLink.Contracts`，只拷 Web 目录编译不过），这会让首次构建慢一些，换来镜像可复现。

### 5. compose 产物补三处默认值

`ConfigureComposeFile` 里做了三件 Aspire 默认不替我们决定的事：

1. **MySQL 健康检查 + `depends_on: service_healthy`**：compose 默认只等「容器启动」，而 Api 启动即跑迁移，抢在 MySQL 初始化完成之前会失败退出，且 compose 不会自动重试。
2. **去掉重复的端口映射**：同时指定 host 端口与外部端点时，Aspire 会生成 `"5080:8080"` 和一条裸的 `"8080"`（等于再随机映射一次），只保留前者。
3. **仪表盘默认不进产物**：它没有认证，谁能访问端口谁就能看 trace（含完整 URL 与 SQL 片段）。需要排障时 `ShortLink__PublishDashboard=true` 显式打开。

### 6. OTLP 端点支持标准环境变量

发布产物里没有 `localhost:19000`，所以 AppHost 在 publish 模式不传 `OpenTelemetry:Endpoint`；Api 侧改为「自定义键优先、`OTEL_EXPORTER_OTLP_ENDPOINT` 兜底」，并按 `OTEL_EXPORTER_OTLP_PROTOCOL` 选择 gRPC 还是 http/protobuf（Collector 常见只开 4318）。

## 验证方式

```bash
dotnet tool restore
dotnet aspire publish -o artifacts/compose          # 7/7 步骤成功，产出 docker-compose.yaml + .env
dotnet aspire do build-api  -o artifacts/compose    # 产出 api:latest 镜像
dotnet aspire do prepare-shortlink                  # 构建镜像 + 填 .env.<environment>
dotnet aspire deploy                                # compose up -d
```

## 后果

**收益**

- 部署文件与本地编排同源：改 AppHost 就同时改了两边，不存在「文档里的 compose 与真实依赖不一致」。
- 密钥一律占位符，`.env` 是唯一的注入点，符合 12-factor。

**代价与已知限制**

- `aspire publish` 只出文件**不建镜像**：`.env` 里 `API_IMAGE` / `API_PORT` 是空的，必须再跑 `prepare-*` 或 `deploy` 才有值——这是官方设计，不是配置错误。
- Kubernetes 集成是 preview；`aspire publish` 到 k8s 时会**要求参数有值**（不像 compose 写占位符），本地要先用 `Parameters__*` 环境变量或交互式输入补齐。`aspire deploy` 还要求 Helm ≥ 4.2。
- Web 镜像的 Dockerfile 在构建时会重新 `dotnet restore`，CI/构建机需要能访问 NuGet 源（容器内不走宿主机的镜像配置）。
- 数据库仍是「容器内 MySQL + 命名卷」，没有多副本与备份方案；真上生产应换成托管数据库，只保留 compose 里的 api/web。
