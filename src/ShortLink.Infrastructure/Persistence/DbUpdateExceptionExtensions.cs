using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Npgsql;

namespace ShortLink.Infrastructure.Persistence;

/// <summary>只识别三种受支持 provider 的明确唯一约束错误，不把外键、长度等写入错误当作幂等命中。</summary>
internal static class DbUpdateExceptionExtensions
{
    public static bool IsUniqueConstraintViolation(this DbUpdateException exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                or MySqlException { Number: 1062 }
                or SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
            {
                return true;
            }
        }

        return false;
    }
}
