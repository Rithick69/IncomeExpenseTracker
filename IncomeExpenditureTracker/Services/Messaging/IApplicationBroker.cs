using System;

namespace IncomeExpenditureTracker.Services.Messaging
{
    /// <summary>
    /// A framework-agnostic messaging broker to decouple UI from backend orchestrators.
    /// </summary>
    public interface IApplicationBroker
    {
        // Publishers use this to drop off a message
        void Send<TMessage>(TMessage message) where TMessage : class;

        // Subscribers (ViewModels) use this to listen for a specific message
        void Register<TMessage>(object recipient, Action<TMessage> handler) where TMessage : class;

        // Subscribers use this to stop listening to a specific message
        void Unregister<TMessage>(object recipient) where TMessage : class;

        // Automatically unregisters all messages for a specific recipient (prevents memory leaks)
        void UnregisterAll(object recipient);
    }
}