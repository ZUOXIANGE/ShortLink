using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ShortLink.Application.Abstractions;
using ShortLink.Contracts;
using StackExchange.Redis;

namespace ShortLink.Infrastructure.Analytics;

/// <summary>单实例点击配额。每个短链的锁只保护一次内存计数，不访问数据库。</summary>
public sealed class InMemoryClickQuota : IClickQuota
{
    private readonly ConcurrentDictionary<long, QuotaState> _states = new();
    private readonly ConcurrentDictionary<Guid, long> _activeReservations = new();
    private readonly ILogger<InMemoryClickQuota> _logger;
    private readonly bool _multiInstanceEnvironment;

    public InMemoryClickQuota(ILogger<InMemoryClickQuota> logger)
    {
        _logger = logger;
        _multiInstanceEnvironment = IsMultiInstanceEnvironment();
        if (_multiInstanceEnvironment)
        {
            _logger.LogError("当前部署可能是多实例，但未配置 Redis；带 MaxClicks 的短链将安全失败，不会使用本地配额超发");
        }
        else
        {
            _logger.LogInformation("点击封顶使用进程内配额，仅适用于单实例；多实例必须配置 Cache:RedisConnectionString");
        }
    }

    public ValueTask<ClickQuotaReservation?> TryReserveAsync(CachedLink link, CancellationToken cancellationToken)
    {
        if (link.MaxClicks is not { } maxClicks)
        {
            return ValueTask.FromResult<ClickQuotaReservation?>(null);
        }

        if (_multiInstanceEnvironment)
        {
            throw new BusinessException(BizCodes.InternalError, "多实例点击封顶需要配置 Redis");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var state = _states.GetOrAdd(link.Id, _ => new QuotaState());
        lock (state.Gate)
        {
            if (!state.Initialized)
            {
                state.Used = Math.Max(link.ClickCount, 0);
                state.Initialized = true;
            }

            if (state.Used >= maxClicks)
            {
                return ValueTask.FromResult<ClickQuotaReservation?>(null);
            }

            state.Used++;
            var reservation = ClickQuotaReservation.Create(link.Id, maxClicks);
            _activeReservations[reservation.ReservationId] = link.Id;
            return ValueTask.FromResult<ClickQuotaReservation?>(reservation);
        }
    }

    public ValueTask ReleaseAsync(ClickQuotaReservation reservation, CancellationToken cancellationToken = default)
        => reservation.TryMarkReleased()
            ? ReleaseAsync(reservation.ReservationId, reservation.LinkId, cancellationToken)
            : ValueTask.CompletedTask;

    public ValueTask ReleaseAsync(Guid reservationId, long linkId, CancellationToken cancellationToken = default)
    {
        // 从活动预留表移除才允许补偿，保证同一 ClickId 重试/重投最多释放一次。
        if (!_activeReservations.TryRemove(reservationId, out _)
            || !_states.TryGetValue(linkId, out var state))
        {
            return ValueTask.CompletedTask;
        }

        lock (state.Gate)
        {
            state.Used = Math.Max(0, state.Used - 1);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(Guid reservationId, long linkId, CancellationToken cancellationToken = default)
    {
        _activeReservations.TryRemove(reservationId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask SynchronizeAsync(long linkId, long actualCount, CancellationToken cancellationToken = default)
    {
        var state = _states.GetOrAdd(linkId, _ => new QuotaState());
        lock (state.Gate)
        {
            state.Used = Math.Max(state.Used, Math.Max(actualCount, 0));
            state.Initialized = true;
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsMultiInstanceEnvironment()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("SHORTLINK_REPLICA_COUNT"), out var replicas) && replicas > 1)
        {
            return true;
        }

        // 与缓存拓扑告警采用同一部署线索：K8s 滚动更新期间即使 replicas=1 也会短暂多实例。
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));
    }

    private sealed class QuotaState
    {
        public object Gate { get; } = new();

        public long Used { get; set; }

        public bool Initialized { get; set; }
    }
}

/// <summary>基于 Redis Lua 的跨实例原子配额。Redis 异常时关闭带封顶短链的跳转，绝不退回本地计数。</summary>
public sealed class RedisClickQuota(IConnectionMultiplexer redis, ILogger<RedisClickQuota> logger) : IClickQuota
{
    private const string ReserveScript = """
        if redis.call('EXISTS', KEYS[2]) == 1 then
            return -1
        end
        local current = redis.call('GET', KEYS[1])
        if not current or tonumber(ARGV[1]) > tonumber(current) then
            current = ARGV[1]
            redis.call('SET', KEYS[1], current)
        end
        if tonumber(current) >= tonumber(ARGV[2]) then
            return 0
        end
        redis.call('INCR', KEYS[1])
        redis.call('SET', KEYS[2], 'active', 'PX', 604800000)
        return 1
        """;

    private const string ReleaseScript = """
        if redis.call('GET', KEYS[2]) ~= 'active' then
            return 0
        end
        local current = tonumber(redis.call('GET', KEYS[1]) or '0')
        if current > 0 then
            redis.call('DECR', KEYS[1])
        end
        redis.call('SET', KEYS[2], 'released', 'PX', 604800000)
        return 1
        """;

    private const string CompleteScript = """
        if redis.call('GET', KEYS[2]) == 'active' then
            redis.call('SET', KEYS[2], 'consumed', 'PX', 604800000)
        end
        return 1
        """;

    private const string SynchronizeScript = """
        local current = tonumber(redis.call('GET', KEYS[1]) or '-1')
        local actual = tonumber(ARGV[1])
        if actual > current then
            redis.call('SET', KEYS[1], actual)
        end
        return 1
        """;

    public async ValueTask<ClickQuotaReservation?> TryReserveAsync(CachedLink link, CancellationToken cancellationToken)
    {
        if (link.MaxClicks is not { } maxClicks)
        {
            return null;
        }

        var reservation = ClickQuotaReservation.Create(link.Id, maxClicks);
        try
        {
            var result = await redis.GetDatabase().ScriptEvaluateAsync(
                    ReserveScript,
                    [Key(link.Id), ReservationKey(reservation.ReservationId)],
                    [Math.Max(link.ClickCount, 0), maxClicks])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (result.IsNull || (long)result < 0)
            {
                throw new RedisException("点击配额脚本返回空结果或预留 ID 冲突");
            }

            return (long)result == 1 ? reservation : null;
        }
        catch (Exception exception) when (IsRedisFailure(exception) && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Redis 点击配额不可用，安全拒绝短链 {LinkId} 的本次跳转", link.Id);
            throw new BusinessException(BizCodes.InternalError, "点击配额服务暂不可用", innerException: exception);
        }
    }

    public ValueTask ReleaseAsync(ClickQuotaReservation reservation, CancellationToken cancellationToken = default)
        => reservation.TryMarkReleased()
            ? ReleaseAsync(reservation.ReservationId, reservation.LinkId, cancellationToken)
            : ValueTask.CompletedTask;

    public async ValueTask ReleaseAsync(Guid reservationId, long linkId, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(
                    ReleaseScript,
                    [Key(linkId), ReservationKey(reservationId)],
                    [])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRedisFailure(exception) && !cancellationToken.IsCancellationRequested)
        {
            // 释放失败只会让短链提前封顶，不会突破硬上限。
            logger.LogError(exception, "Redis 点击配额补偿失败：link={LinkId} reservation={ReservationId}",
                linkId, reservationId);
        }
    }

    public async ValueTask CompleteAsync(Guid reservationId, long linkId, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(
                    CompleteScript,
                    [Key(linkId), ReservationKey(reservationId)],
                    [])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRedisFailure(exception) && !cancellationToken.IsCancellationRequested)
        {
            // 完成标记失败保留 active 状态；重复批若失败可能补偿一次，后续按实际计数再次校准。
            logger.LogError(exception, "Redis 点击配额完成标记失败：link={LinkId} reservation={ReservationId}",
                linkId, reservationId);
        }
    }

    public async ValueTask SynchronizeAsync(long linkId, long actualCount, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().ScriptEvaluateAsync(
                    SynchronizeScript,
                    [Key(linkId)],
                    [Math.Max(actualCount, 0)])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRedisFailure(exception) && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Redis 点击配额校准失败：link={LinkId} actual={ActualCount}", linkId, actualCount);
        }
    }

    private static RedisKey Key(long linkId) => $"shortlink:click-quota:{linkId}";

    private static RedisKey ReservationKey(Guid reservationId) => $"shortlink:click-quota-reservation:{reservationId:N}";

    private static bool IsRedisFailure(Exception exception)
        => exception is RedisException or TimeoutException or ObjectDisposedException or InvalidOperationException;
}
