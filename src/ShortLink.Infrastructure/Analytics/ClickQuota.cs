using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ShortLink.Application.Abstractions;
using ShortLink.Contracts;
using StackExchange.Redis;

namespace ShortLink.Infrastructure.Analytics;

/// <summary>单实例点击配额。每个短链的锁只保护一次内存计数，不访问数据库。</summary>
public sealed class InMemoryClickQuota : IClickQuota
{
    // 闲置状态可以安全丢弃：下一次预留会用 CachedLink.ClickCount（数据库计数）重新初始化，
    // 不会把已用量算少。定期清扫，避免「建了又删」的短链在进程内留下永久条目。
    private const long StateSweepIntervalMs = 10 * 60 * 1000;
    private const long IdleStateTtlMs = 24 * 60 * 60 * 1000;

    private readonly ConcurrentDictionary<long, QuotaState> _states = new();
    private readonly ConcurrentDictionary<Guid, long> _activeReservations = new();
    private readonly bool _multiInstanceEnvironment;
    private long _lastSweepMs;

    public InMemoryClickQuota(ILogger<InMemoryClickQuota> logger)
    {
        _multiInstanceEnvironment = IsMultiInstanceEnvironment();
        if (_multiInstanceEnvironment)
        {
            logger.LogError("当前部署可能是多实例，但未配置 Redis；带 MaxClicks 的短链将安全失败，不会使用本地配额超发");
        }
        else
        {
            logger.LogInformation("点击封顶使用进程内配额，仅适用于单实例；多实例必须配置 Cache:RedisConnectionString");
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
        SweepIdleStatesIfNeeded();
        var state = _states.GetOrAdd(link.Id, _ => new QuotaState());
        lock (state.Gate)
        {
            state.LastTouchedMs = Environment.TickCount64;
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
            state.ActiveReservations++;
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
            state.ActiveReservations = Math.Max(0, state.ActiveReservations - 1);
            state.Used = Math.Max(0, state.Used - 1);
            state.LastTouchedMs = Environment.TickCount64;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteAsync(Guid reservationId, long linkId, CancellationToken cancellationToken = default)
    {
        if (_activeReservations.TryRemove(reservationId, out _)
            && _states.TryGetValue(linkId, out var state))
        {
            lock (state.Gate)
            {
                state.ActiveReservations = Math.Max(0, state.ActiveReservations - 1);
                state.LastTouchedMs = Environment.TickCount64;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask SynchronizeAsync(long linkId, long actualCount, CancellationToken cancellationToken = default)
    {
        var state = _states.GetOrAdd(linkId, _ => new QuotaState());
        lock (state.Gate)
        {
            state.Used = Math.Max(state.Used, Math.Max(actualCount, 0));
            state.Initialized = true;
            state.LastTouchedMs = Environment.TickCount64;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 丢弃长时间没有活动的链路状态。只清理没有在途预留的条目——
    /// 在途预留在下一次清扫或重新预留时会与数据库计数重新对齐。
    /// </summary>
    private void SweepIdleStatesIfNeeded()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastSweepMs);
        if (last != 0 && now - last < StateSweepIntervalMs)
        {
            return;
        }

        // 只有一个线程负责本轮清扫，避免所有请求同时遍历字典。
        if (Interlocked.CompareExchange(ref _lastSweepMs, now, last) != last)
        {
            return;
        }

        foreach (var pair in _states)
        {
            var state = pair.Value;
            lock (state.Gate)
            {
                if (state.ActiveReservations == 0 && now - state.LastTouchedMs >= IdleStateTtlMs)
                {
                    // 用 KeyValuePair 重载删除，避免把并发替换后的新状态误删。
                    _states.TryRemove(pair);
                }
            }
        }
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
        public QuotaState()
        {
            LastTouchedMs = Environment.TickCount64;
        }

        public object Gate { get; } = new();

        public long Used { get; set; }

        public bool Initialized { get; set; }

        /// <summary>在途预留数；大于 0 时不能被清扫，否则重新初始化会把已用量算少。</summary>
        public int ActiveReservations { get; set; }

        public long LastTouchedMs { get; set; }
    }
}

/// <summary>基于 Redis Lua 的跨实例原子配额。Redis 异常时关闭带封顶短链的跳转，绝不退回本地计数。</summary>
public sealed class RedisClickQuota(IConnectionMultiplexer redis, ILogger<RedisClickQuota> logger) : IClickQuota
{
    // 计数键和预留键都必须带过期时间，否则删除或长期闲置的短链会在 Redis 留下永久键。
    // 计数键过期后，下一次预留用 CachedLink.ClickCount（已落库的计数）重新初始化，
    // 不会把已用量算少，代价只是丢掉尚未落库的在途预留。
    private const long CounterTtlMs = 7 * 24 * 60 * 60 * 1000;

    private const long ReservationTtlMs = 7 * 24 * 60 * 60 * 1000;

    private const string ReserveScript = """
        if redis.call('EXISTS', KEYS[2]) == 1 then
            return -1
        end
        local counter = redis.call('GET', KEYS[1])
        local effective = counter
        if not effective or tonumber(ARGV[1]) > tonumber(effective) then
            effective = ARGV[1]
        end
        if tonumber(effective) >= tonumber(ARGV[2]) then
            -- 已封顶也要刷新存活时间，避免「封顶后再无点击」的键永久留库。
            if counter then
                redis.call('PEXPIRE', KEYS[1], ARGV[3])
            end
            return 0
        end
        redis.call('SET', KEYS[1], effective, 'PX', ARGV[3])
        redis.call('INCR', KEYS[1])
        redis.call('SET', KEYS[2], 'active', 'PX', ARGV[4])
        return 1
        """;

    private const string ReleaseScript = """
        if redis.call('GET', KEYS[2]) ~= 'active' then
            return 0
        end
        local current = tonumber(redis.call('GET', KEYS[1]) or '0')
        if current > 0 then
            redis.call('DECR', KEYS[1])
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
        end
        redis.call('SET', KEYS[2], 'released', 'PX', ARGV[2])
        return 1
        """;

    private const string CompleteScript = """
        if redis.call('GET', KEYS[2]) == 'active' then
            redis.call('SET', KEYS[2], 'consumed', 'PX', ARGV[1])
        end
        return 1
        """;

    private const string SynchronizeScript = """
        local current = tonumber(redis.call('GET', KEYS[1]) or '-1')
        local actual = tonumber(ARGV[1])
        if actual > current then
            redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
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
                    [Math.Max(link.ClickCount, 0), maxClicks, CounterTtlMs, ReservationTtlMs])
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
                    [CounterTtlMs, ReservationTtlMs])
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
                    [ReservationTtlMs])
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
                    [Math.Max(actualCount, 0), CounterTtlMs])
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
