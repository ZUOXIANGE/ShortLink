# ADR 0004：多数据库 provider 与迁移程序集分离

- 状态：已采纳
- 日期：2026-11-11
- 相关：`Directory.Packages.props`、`src/ShortLink.Infrastructure/InfrastructureServiceCollectionExtensions.cs`

## 背景

平台需要三种运行形态：

1. **生产**：MySQL 8（团队既有运维体系）。
2. **本地开发/CI**：零依赖，不能要求每位开发者装 MySQL。
3. **备选**：PostgreSQL（部分客户环境）。

需求同时包括：`dotnet run` 不改代码即可切换 provider；EF Core 迁移不能混用不同方言的 DDL。

## 决策

1. **Provider 由配置决定**（`Database:Provider` = `MySql` | `Sqlite` | `PostgreSql`），在 `AddDbContext` 的 `IServiceProvider` 回调里运行时解析，便于测试宿主与容器环境覆盖。
2. **每个 provider 一个迁移程序集**：`ShortLink.Infrastructure.Migrations.{Sqlite,MySql,PostgreSql}`，各自带 `IDesignTimeDbContextFactory<T>`，互相不引用。
3. **MySQL 驱动使用 `Microting.EntityFrameworkCore.MySql` 10.0.14**，而不是官方 `MySql.EntityFrameworkCore` 或 `Pomelo.EntityFrameworkCore.MySql`。

## 理由

- **为什么不用 Pomelo 9.0.0**：其依赖区间为 `[9.0.0, 9.0.999]`，无法与 EF Core 10 共存；官方 Pomelo 尚未发布 EF 10 版本。
- **为什么不用 Oracle 官方 `MySql.EntityFrameworkCore`**：许可证为 GPL-2.0 + FOSS 例外，商业分发需评估；且 API 与 Pomelo 不同，替换成本高。
- **为什么选 Microting 分支**：MIT 许可；版本 10.0.14，依赖 `Microsoft.EntityFrameworkCore.Relational [10.0.14, 10.0.999]` 与 `MySqlConnector 2.6.2`，与本项目 EF Core 10.0.15 兼容；API 与 Pomelo 完全一致（`UseMySql` / `MySqlServerVersion`），迁移写法无需变化。
- **为什么迁移按 provider 分程序集**：EF Core 的迁移不区分 provider，同一个迁移类无法同时生成 MySQL 的 `datetime(6)` 与 SQLite 的 `TEXT`；混用会在切换数据库时直接失败。分程序集后由 `MigrationsAssembly(...)` 指定，各库互不干扰。
- **为什么用固定 `MySqlServerVersion` 而非 `ServerVersion.AutoDetect`**：AutoDetect 会在启动期连库探测，拉长冷启动并把数据库不可用变成启动失败；版本通过配置显式声明更可控。

## 后果

**正面**

- `dotnet run` 默认即可跑通（开发环境 SQLite），生产切 MySQL 只改配置。
- 迁移历史按方言隔离，不会互相污染。
- 多 provider 迁移程序集必须随宿主一起发布，否则 EF 反射加载会抛 `FileNotFoundException`（`ShortLink.Api.csproj` 已显式引用三者）。

**负面 / 代价**

- 每个 schema 变更需要执行三次 `dotnet ef migrations add`（命令见 `docs/database-design.md` §6.2）。
- Microting 是社区维护分支，升级节奏跟随 .NET 主版本，需要关注其发布；若官方 Pomelo 发布 EF 10 版本，可平移替换（API 一致）。
- 三套迁移增加了 CI 时间与审阅成本。
