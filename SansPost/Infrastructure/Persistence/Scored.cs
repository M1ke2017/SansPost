using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace SansPost.Infrastructure.Persistence
{
    // Wiersz z wynikiem rankingu liczonym w SQL (search relevance, popularność).
    // Score jest liczbą całkowitą — deterministyczny klucz sortowania i kursora.
    public sealed class Scored<TEntity>
    {
        public TEntity Entity { get; set; } = default!;
        public long Score { get; set; }
    }

    public sealed record ScoredResult<TResult>(TResult Item, long Score);

    public static class ScoredProjection
    {
        // Składa istniejącą projekcję encji (np. DTO z licznikami) z wynikiem rankingu w jedno wyrażenie,
        // które EF tłumaczy na JEDEN SELECT — bez duplikowania definicji projekcji.
        public static Expression<Func<Scored<TEntity>, ScoredResult<TResult>>> Of<TEntity, TResult>(
            Expression<Func<TEntity, TResult>> projection)
        {
            var row = Expression.Parameter(typeof(Scored<TEntity>), "row");
            var item = ReplacingExpressionVisitor.Replace(
                projection.Parameters[0],
                Expression.Property(row, nameof(Scored<TEntity>.Entity)),
                projection.Body);

            var constructor = typeof(ScoredResult<TResult>).GetConstructor(new[] { typeof(TResult), typeof(long) })!;
            var body = Expression.New(constructor, item, Expression.Property(row, nameof(Scored<TEntity>.Score)));

            return Expression.Lambda<Func<Scored<TEntity>, ScoredResult<TResult>>>(body, row);
        }
    }
}
