using System;
using System.Linq;
using Xunit;
using Moq;
using IncomeExpenditureTracker.ViewModels;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Tests.ViewModels
{
    public class MainWindowViewModelTests
    {
        public MainWindowViewModelTests()
        {
            // Bypass the Avalonia UI Thread for headless testing
            ViewModelBase.IsTestEnvironment = true;
        }

        [Fact]
        public void OnToastReceived_BrokerMessage_AddsToastToCollection()
        {
            // Arrange
            var mockBroker = new Mock<IApplicationBroker>();
            Action<ToastNotificationMessage>? capturedCallback = null;

            // Intercept the ViewModel's subscription to ToastNotificationMessage
            mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<ToastNotificationMessage>>()))
                      .Callback<object, Action<ToastNotificationMessage>>((subscriber, callback) =>
                      {
                          capturedCallback = callback;
                      });

            var viewModel = new MainWindowViewModel(mockBroker.Object);

            // Act: Simulate a background orchestrator sending a success toast
            var message = new ToastNotificationMessage("Database backed up!", NotificationType.Success);
            capturedCallback?.Invoke(message);

            // Assert: The toast should instantly appear in the observable collection
            Assert.Single(viewModel.Toasts);
            Assert.Equal("Database backed up!", viewModel.Toasts.First().Message);
            Assert.Equal(NotificationType.Success, viewModel.Toasts.First().Type);
        }

        [Fact]
        public void DismissToastCommand_RemovesSpecificToastFromCollection()
        {
            // Arrange
            var mockBroker = new Mock<IApplicationBroker>();
            var viewModel = new MainWindowViewModel(mockBroker.Object);

            // Manually inject two toasts into the UI stack
            var toast1 = new ToastAlert("Error 1", NotificationType.Error);
            var toast2 = new ToastAlert("Error 2", NotificationType.Error);

            viewModel.Toasts.Add(toast1);
            viewModel.Toasts.Add(toast2);

            // Act: The user clicks the "X" on the first toast
            viewModel.DismissToastCommand.Execute(toast1.Id);

            // Assert: Only the second toast should remain
            Assert.Single(viewModel.Toasts);
            Assert.Equal(toast2.Id, viewModel.Toasts.First().Id);
        }
    }
}