namespace DesktopOrganizer.Services;

/// <summary>Итог восстановления одного <see cref="RestoreTarget"/> (зеркало DesktopIconService.RestoreResult).</summary>
public enum RestoreOutcome
{
    Restored,
    FileGone,
    Failed,
}

/// <summary>
/// Чистая политика восстановления, отделённая от файлового ввода-вывода: по итогам Restore каждого
/// target'а решает, какие строки БД пометить восстановленными и каким признать общий результат.
/// Раньше эта логика жила внутри App.RestoreAllHidden вперемешку с I/O и не покрывалась тестами,
/// хотя именно здесь жили прошлые регрессии (очистка флагов после неуспеха, проглатывание сбоя
/// чтения БД). См. tests/DesktopOrganizer.Tests/RecoveryPlanTests.cs.
/// </summary>
public static class RecoveryPlan
{
    /// <param name="Failed">Число невосстановленных, либо -1 если источник прочитан не полностью (журнал/БД).</param>
    /// <param name="ClearedIds">Id строк БД, которые можно пометить восстановленными (Restored/FileGone).</param>
    /// <param name="Success">true только если всё восстановлено И оба источника прочитаны.</param>
    public sealed record Result(int Failed, IReadOnlyList<long> ClearedIds, bool Success);

    /// <summary>
    /// <paramref name="outcomes"/> идёт параллельно <paramref name="plan"/>. Флаги БД чистятся ТОЛЬКО
    /// для target'ов с итогом Restored/FileGone и только если БД прочитана полностью.
    /// Если журнал или БД прочитаны не полностью (<paramref name="journalReadOk"/>/<paramref name="dbReadOk"/>
    /// = false) — результат -1 (не успех) и список очистки пуст: при сбое чтения БД у нас нет полной
    /// картины её строк (в план попадают только journal-only targets без ItemIds), поэтому чистить
    /// нечего и не следует. -1 не даёт деинсталлятору счесть восстановление успешным и удалить данные.
    /// </summary>
    public static Result Resolve(
        IReadOnlyList<RestoreTarget> plan,
        IReadOnlyList<RestoreOutcome> outcomes,
        bool journalReadOk,
        bool dbReadOk)
    {
        if (plan.Count != outcomes.Count)
            throw new ArgumentException("outcomes должен идти параллельно plan", nameof(outcomes));

        if (!journalReadOk || !dbReadOk)
            return new Result(-1, Array.Empty<long>(), false);

        var failed = 0;
        var cleared = new List<long>();
        for (var i = 0; i < plan.Count; i++)
        {
            if (outcomes[i] == RestoreOutcome.Failed) { failed++; continue; }
            cleared.AddRange(plan[i].ItemIds); // файл мог быть в нескольких коробках — чистим все строки
        }

        return new Result(failed, cleared, failed == 0);
    }
}
