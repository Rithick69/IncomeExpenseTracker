using System;
using CommunityToolkit.Mvvm.Messaging;

namespace IncomeExpenditureTracker.Services.Messaging
{
    /// <summary>
    /// Wraps the CommunityToolkit WeakReferenceMessenger to prevent tight coupling to third-party libraries.
    /// WeakReferenceMessenger guarantees that if a UI View is closed, it gets garbage collected
    /// even if we forget to manually unsubscribe.
    /// </summary>
    public class ToolkitMessengerAdapter : IApplicationBroker
    {
        public void Send<TMessage>(TMessage message) where TMessage : class
        {
            // Drops the message into the global toolkit messenger
            WeakReferenceMessenger.Default.Send(message);
        }

        public void Register<TMessage>(object recipient, Action<TMessage> handler) where TMessage : class
        {
            // Connects our framework-agnostic Action to the Toolkit's internal handler
            WeakReferenceMessenger.Default.Register<TMessage>(recipient, (r, m) => handler(m));
        }

        public void Unregister<TMessage>(object recipient) where TMessage : class
        {
            WeakReferenceMessenger.Default.Unregister<TMessage>(recipient);
        }

        public void UnregisterAll(object recipient)
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }
}