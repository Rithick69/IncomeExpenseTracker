using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

// This is your centralized error space.
// It records structured failures across the Loading, Preview, and Import phases.
// You can register IStatementErrorSink as a Singleton in your dependency injection container so StatementManager
// and your test assertions share the exact same instance.

namespace IncomeExpenditureTracker.Tests.Observability
{
    public enum StatementProcessingPhase
    {
        Loading,
        Preview,
        Import
    }

    public record StatementError(
        StatementProcessingPhase Phase,
        string? FileName,
        string? SheetName,
        int? RowIndex,
        string UserFriendlyMessage,
        Exception? TechnicalException,
        DateTime Timestamp
    );

    public interface IStatementErrorSink
    {
        event Action<StatementError>? OnErrorCaptured;
        void Capture(StatementProcessingPhase phase, string message, Exception? ex = null, string? fileName = null, string? sheetName = null, int? row = null);
        IReadOnlyCollection<StatementError> GetAllErrors();
        void Clear();
    }

    public class StatementErrorSink : IStatementErrorSink
    {
        private readonly ConcurrentBag<StatementError> _errors = new();

        public event Action<StatementError>? OnErrorCaptured;

        public void Capture(
            StatementProcessingPhase phase,
            string message,
            Exception? ex = null,
            string? fileName = null,
            string? sheetName = null,
            int? row = null)
        {
            var error = new StatementError(
                Phase: phase,
                FileName: fileName,
                SheetName: sheetName,
                RowIndex: row,
                UserFriendlyMessage: message,
                TechnicalException: ex,
                Timestamp: DateTime.UtcNow
            );

            _errors.Add(error);
            OnErrorCaptured?.Invoke(error);
        }

        public IReadOnlyCollection<StatementError> GetAllErrors() => _errors;

        public void Clear() => _errors.Clear();
    }
}