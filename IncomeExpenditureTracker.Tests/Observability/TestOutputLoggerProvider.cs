using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

// This provider bridges Microsoft's ILogger<T> directly into xUnit's ITestOutputHelper.
// When your tests run, you will see the exact step-by-step console output from your FieldMapper, DescriptionParser, TagEngine,
// and database services right in your test runner window.

namespace IncomeExpenditureTracker.Tests.Observability
{
    public class TestOutputLoggerProvider : ILoggerProvider
    {
        private readonly ITestOutputHelper _output;
        private readonly ConcurrentDictionary<string, ILogger> _loggers = new();

        public TestOutputLoggerProvider(ITestOutputHelper output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new TestOutputLogger(name, _output));
        }

        public void Dispose()
        {
            _loggers.Clear();
            GC.SuppressFinalize(this);
        }

        private class TestOutputLogger : ILogger
        {
            private readonly string _categoryName;
            private readonly ITestOutputHelper _output;

            public TestOutputLogger(string categoryName, ITestOutputHelper output)
            {
                // Keep the category name short for readable test logs
                _categoryName = categoryName.Contains('.')
                    ? categoryName.Substring(categoryName.LastIndexOf('.') + 1)
                    : categoryName;
                _output = output;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var message = formatter(state, exception);
                var logLine = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [{logLevel,-11}] [{_categoryName}] {message}";

                try
                {
                    _output.WriteLine(logLine);
                    if (exception != null)
                    {
                        _output.WriteLine($"EXCEPTION: {exception.GetType().Name}: {exception.Message}");
                        _output.WriteLine(exception.StackTrace);
                    }
                }
                catch (InvalidOperationException)
                {
                    // This occurs if xUnit attempts to flush logs after the test execution context has finished.
                    // We swallow it safely to prevent test teardown crashes.
                }
            }
        }
    }
}