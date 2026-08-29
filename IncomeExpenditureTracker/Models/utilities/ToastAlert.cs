using System;

namespace IncomeExpenditureTracker.Models
{

    // =========================================================================
    // UI BINDING MODEL: The actual object stacked in the Avalonia UI
    // =========================================================================
    public class ToastAlert
    {
        // A unique ID is required so the user can click "X" to dismiss a specific toast
        public Guid Id { get; } = Guid.NewGuid();

        public string Message { get; }
        public NotificationType Type { get; }

        public ToastAlert(string message, NotificationType type)
        {
            Message = message;
            Type = type;
        }
    }
}