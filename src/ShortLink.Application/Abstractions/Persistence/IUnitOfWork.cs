namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>工作单元：由基础设施层以 DbContext 实现。</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken);
}
