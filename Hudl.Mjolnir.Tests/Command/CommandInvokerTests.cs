﻿using Hudl.Mjolnir.Command;
using System;
using System.Threading.Tasks;
using Hudl.Mjolnir.External;
using Hudl.Mjolnir.Key;
using Moq;
using Xunit;
using System.Threading;
using Hudl.Mjolnir.Tests.Helper;
using Hudl.Mjolnir.Breaker;
using Hudl.Mjolnir.Bulkhead;
using Hudl.Config;

namespace Hudl.Mjolnir.Tests.Command
{
    public class CommandInvokerTests
    {
        public class InvokeAsync : TestFixture
        {
            [Fact]
            public async Task WhenCommandAlreadyInvoked_FailureModeIsThrow_Throws()
            {
                // A command instance should only be invoked once. The failure mode
                // here shouldn't actually matter; there's a similar test below
                // that asserts the same throwing behavior here with a different
                // failure mode.

                // The command used here doesn't matter.
                var command = new NoOpAsyncCommand();
                var invoker = new CommandInvoker();
                var failureMode = OnFailure.Throw;

                // This first call shouldn't throw, but the second one should.
                await invoker.InvokeAsync(command, failureMode);

                // The timeout shouldn't matter. The invoked-once check should be
                // one of the first validations performed.
                await Assert.ThrowsAsync(typeof(InvalidOperationException), () => invoker.InvokeAsync(command, failureMode));
            }

            [Fact]
            public async Task WhenCommandAlreadyInvoked_FailureModeIsReturn_StillThrows()
            {
                // Even if the failure mode isn't "Throw", we want to throw in
                // this situation. This is a bug on the calling side, since Command
                // instances shouldn't be reused. The exception thrown should help
                // the caller see that problem and fix it.

                // The command used here doesn't matter.
                var command = new NoOpAsyncCommand();
                var invoker = new CommandInvoker();
                var failureMode = OnFailure.Return;

                // This first call shouldn't throw, but the second one should.
                await invoker.InvokeAsync(command, failureMode);

                // The timeout shouldn't matter. The invoked-once check should be
                // one of the first validations performed.
                await Assert.ThrowsAsync(typeof(InvalidOperationException), () => invoker.InvokeAsync(command, failureMode));
            }

            [Fact]
            public async Task ExecuteSuccessful_FailureModeThrow_ReturnsWrappedResult()
            {
                // Successful command execution should return a wrapped CommandResult.

                const bool expectedResultValue = true;
                var command = new NoOpAsyncCommand();
                
                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())).Returns(Task.FromResult(expectedResultValue));

                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

                // We're testing OnFailure.Throws here. Mainly, it shouldn't throw if we're successful.
                var result = await invoker.InvokeAsync(command, OnFailure.Throw);

                mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())); // TODO test CancellationToken more accurately?
                mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOpAsync execute", "RanToCompletion", It.IsAny<TimeSpan>()));
                Assert.Equal(CommandCompletionStatus.RanToCompletion, result.Status);
                Assert.Null(result.Exception);
                Assert.Equal(true, result.Value);
            }

            [Fact]
            public async Task ExecuteSuccessful_FailureModeReturn_ReturnsWrappedResult()
            {
                // Successful command execution should return a wrapped CommandResult.

                const bool expectedResultValue = true;
                var command = new NoOpAsyncCommand();

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())).Returns(Task.FromResult(expectedResultValue));

                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

                // We're testing OnFailure.Return here. The failure mode shouldn't have any bearing
                // on what happens during successful execution.
                var result = await invoker.InvokeAsync(command, OnFailure.Return);

                mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())); // TODO test CancellationToken more accurately?
                mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOpAsync execute", "RanToCompletion", It.IsAny<TimeSpan>()));
                Assert.Equal(CommandCompletionStatus.RanToCompletion, result.Status);
                Assert.Null(result.Exception);
                Assert.Equal(true, result.Value);
            }

            [Fact]
            public async Task ExecutionCanceled_FailureModeThrow_Throws()
            {
                // When failure mode is Throw, cancellation should result in an exception. Note
                // that cancellation is different from timeouts (tested below). Timeouts are a
                // specific form of cancellation, and get logged/tracked as timeouts instead of
                // general "cancellations".

                var command = new NoOpAsyncCommand();

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();

                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);
                var expiredToken = new CancellationToken(true);

                // We're testing the combination of OnFailure.Throw and the expired token here.
                var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => invoker.InvokeAsync(command, OnFailure.Throw, expiredToken));

                // We shouldn't have even attempted the execution since the token was expired upon
                // entering the method.
                mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>()), Times.Never);

                mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOpAsync execute", "Canceled", It.IsAny<TimeSpan>()));
                Assert.Equal("test.NoOpAsync", exception.Data["Command"]);
                Assert.Equal(CommandCompletionStatus.Canceled, exception.Data["Status"]);
                Assert.Equal(GroupKey.Named("test"), exception.Data["Breaker"]);
                Assert.Equal(GroupKey.Named("test"), exception.Data["Bulkhead"]);
                Assert.Equal("Token", exception.Data["TimeoutMillis"]);
                Assert.True((double) exception.Data["ElapsedMillis"] >= 0);
            }

            [Fact]
            public async Task ExecutionCanceled_FailureModeReturn_Returns()
            {
                // When failure mode is Return, cancellation shouldn't throw, and should instead
                // return a result object with error information. Note that cancellation is
                // different from timeouts (tested below). Timeouts are a specific form of
                // cancellation, and get logged/tracked as timeouts instead of general
                // "cancellations".

                // When a command fails and failure mode is Return, the value of the result will
                // be default(Type). Our test Command uses a bool, so we should expect a false.
                // Callers shouldn't be checking the result, anyway - if they're using the
                // Return failure mode, they should be checking for success first.
                const bool expectedResultValue = default(bool);
                var command = new NoOpAsyncCommand();

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();

                // We're testing the combination of OnFailure.Return and the expired token here.
                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);
                var expiredToken = new CancellationToken(true);
                
                // Shouldn't throw.
                var result = await invoker.InvokeAsync(command, OnFailure.Return, expiredToken);

                // We shouldn't have even attempted the execution since the token was expired upon
                // entering the method.
                mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>()), Times.Never);
                
                mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOpAsync execute", "Canceled", It.IsAny<TimeSpan>()));
                
                Assert.NotNull(result.Exception);
                Assert.True(result.Exception.GetType() == typeof(OperationCanceledException));
                Assert.Equal("test.NoOpAsync", result.Exception.Data["Command"]);
                Assert.Equal(CommandCompletionStatus.Canceled, result.Exception.Data["Status"]);
                Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Breaker"]);
                Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Bulkhead"]);
                Assert.Equal("Token", result.Exception.Data["TimeoutMillis"]);
                Assert.True((double) result.Exception.Data["ElapsedMillis"] >= 0);

                Assert.Equal(CommandCompletionStatus.Canceled, result.Status);
                Assert.Equal(expectedResultValue, result.Value);
            }

            [Fact]
            public async Task TimeoutBeforeExecution_FailureModeThrow_Throws()
            {
                // When failure mode is Throw, timeouts should result in an exception. Timeouts
                // are a specific form of cancellation, and get logged/tracked as timeouts instead
                // of general "cancellations".

                var command = new DelayAsyncCommand(1);

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                
                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);
                
                // We're testing the combination of OnFailure.Throw and the timeout here.
                var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => invoker.InvokeAsync(command, OnFailure.Throw, 0));

                // We shouldn't have even attempted the execution since the timeout expired by the
                // time we entered the method.
                mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>()), Times.Never);

                mockStats.Verify(m => m.Elapsed("mjolnir command test.DelayAsync execute", "TimedOut", It.IsAny<TimeSpan>()));
                Assert.Equal("test.DelayAsync", exception.Data["Command"]);
                Assert.Equal(CommandCompletionStatus.TimedOut, exception.Data["Status"]);
                Assert.Equal(GroupKey.Named("test"), exception.Data["Breaker"]);
                Assert.Equal(GroupKey.Named("test"), exception.Data["Bulkhead"]);
                Assert.Equal(0, exception.Data["TimeoutMillis"]);
                Assert.True((double)exception.Data["ElapsedMillis"] >= 0);
            }

            [Fact]
            public async Task TimeoutBeforeExecution_FailureModeReturn_Returns()
            {
                // When failure mode is Return, cancellation shouldn't throw, and should instead
                // return a result object with error information. Timeouts are a specific form of
                // cancellation, and get logged/tracked as timeouts instead of general
                // "cancellations".

                // When a command fails and failure mode is Return, the value of the result will
                // be default(Type). Our test Command uses a bool, so we should expect a false.
                // Callers shouldn't be checking the result, anyway - if they're using the
                // Return failure mode, they should be checking for success first.
                const bool expectedResultValue = default(bool);
                
                var command = new DelayAsyncCommand(1);

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();

                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

                // We're testing the combination of OnFailure.Return and the timeout here.
                var result = await invoker.InvokeAsync(command, OnFailure.Return, 0);

                // We shouldn't have even attempted the execution since the timeout expired by the
                // time we entered the method.
                mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>()), Times.Never);

                mockStats.Verify(m => m.Elapsed("mjolnir command test.DelayAsync execute", "TimedOut", It.IsAny<TimeSpan>()));

                Assert.NotNull(result.Exception);
                Assert.True(result.Exception.GetType() == typeof(OperationCanceledException));
                Assert.Equal("test.DelayAsync", result.Exception.Data["Command"]);
                Assert.Equal(CommandCompletionStatus.TimedOut, result.Exception.Data["Status"]);
                Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Breaker"]);
                Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Bulkhead"]);
                Assert.Equal(0, result.Exception.Data["TimeoutMillis"]);
                Assert.True((double) result.Exception.Data["ElapsedMillis"] >= 0);

                Assert.Equal(CommandCompletionStatus.TimedOut, result.Status);
                Assert.Equal(expectedResultValue, result.Value);
            }

            [Fact]
            public async Task CancellationIgnored_InvokesRegardlessOfInvocationToken()
            {
                // When doing local development, timeouts often get hit after sitting on a
                // breakpoint for too long. An optional configuration can be set to ignore
                // cancellation and work around that. If the config is set, all cancellation
                // (regardless of source) is ignored.

                var command = new DelayAsyncCommand(1);
                var expiredToken = new CancellationToken(true);

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())).Returns(Task.FromResult(true));
                
                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object, new TransientConfigurableValue<bool>(true));

                // Even though we pass a token, the config value on the invoker should prevent the
                // token from being used/checked. This shouldn't throw.
                var result = await invoker.InvokeAsync(command, OnFailure.Throw, expiredToken);
                
                Assert.Equal(CommandCompletionStatus.RanToCompletion, result.Status);
            }

            [Fact]
            public async Task CancellationIgnored_InvokesRegardlessOfInvocationTimeout()
            {
                // When doing local development, timeouts often get hit after sitting on a
                // breakpoint for too long. An optional configuration can be set to ignore
                // cancellation and work around that. If the config is set, all cancellation
                // (regardless of source) is ignored.

                var command = new NoOpAsyncCommand();

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())).Returns(Task.FromResult(true));

                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object, new TransientConfigurableValue<bool>(true));
                
                // Even though we pass a timeout, the config value on the invoker should prevent the
                // timeout from being used/checked. This shouldn't throw.
                var result = await invoker.InvokeAsync(command, OnFailure.Throw, 0);

                Assert.Equal(CommandCompletionStatus.RanToCompletion, result.Status);
            }

            [Fact]
            public async Task CancellationIgnoredAndExecutionFaulted_MarksTimeoutIgnoredOnExceptionData()
            {
                // If a command faults and cancellation is disabled, we should still log the
                // timeout we would have used on the exception. This is a fairly inconsequential
                // test, but we might as well make sure what we're logging is accurate.

                var expectedException = new ExpectedTestException("Expected");
                var command = new NoOpAsyncCommand();

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())).Throws(expectedException);

                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object, new TransientConfigurableValue<bool>(true));

                // We're testing the combination of OnFailure.Throw and the exceptions here.
                var exception = await Assert.ThrowsAsync<ExpectedTestException>(() => invoker.InvokeAsync(command, OnFailure.Throw));

                Assert.NotNull(exception);
                Assert.Equal("Ignored", exception.Data["TimeoutMillis"]);
            }

            [Fact]
            public async Task ExecutionFaulted_FailureModeThrow_Throws()
            {
                // When failure mode is Throw, faults should result in an exception.

                var expectedException = new ExpectedTestException("Expected");
                var command = new NoOpAsyncCommand();

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())).Throws(expectedException);
                
                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

                // We're testing the combination of OnFailure.Throw and the exceptions here.
                var exception = await Assert.ThrowsAsync<ExpectedTestException>(() => invoker.InvokeAsync(command, OnFailure.Throw));

                mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOpAsync execute", "Faulted", It.IsAny<TimeSpan>()));
                Assert.Equal("test.NoOpAsync", exception.Data["Command"]);
                Assert.Equal(CommandCompletionStatus.Faulted, exception.Data["Status"]);
                Assert.Equal(GroupKey.Named("test"), exception.Data["Breaker"]);
                Assert.Equal(GroupKey.Named("test"), exception.Data["Bulkhead"]);
                Assert.Equal((int) command.DetermineTimeout().TotalMilliseconds, exception.Data["TimeoutMillis"]);
                Assert.True((double)exception.Data["ElapsedMillis"] >= 0);
            }

            [Fact]
            public async Task ExecutionFaulted_FailureModeReturn_Returns()
            {
                // When failure mode is Return, faults shouldn't throw, and should instead
                // return a result object with error information.

                // When a command fails and failure mode is Return, the value of the result will
                // be default(Type). Our test Command uses a bool, so we should expect a false.
                // Callers shouldn't be checking the result, anyway - if they're using the
                // Return failure mode, they should be checking for success first.
                const bool expectedResultValue = default(bool);

                var expectedException = new ExpectedTestException("Expected");
                var command = new NoOpAsyncCommand();

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())).Throws(expectedException);

                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

                // We're testing the combination of OnFailure.Return and the exceptions here.
                var result = await invoker.InvokeAsync(command, OnFailure.Return);

                mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOpAsync execute", "Faulted", It.IsAny<TimeSpan>()));

                Assert.NotNull(result.Exception);
                Assert.Equal(expectedException, result.Exception);
                Assert.Equal("test.NoOpAsync", result.Exception.Data["Command"]);
                Assert.Equal(CommandCompletionStatus.Faulted, result.Exception.Data["Status"]);
                Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Breaker"]);
                Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Bulkhead"]);
                Assert.Equal((int)command.DetermineTimeout().TotalMilliseconds, result.Exception.Data["TimeoutMillis"]);
                Assert.True((double) result.Exception.Data["ElapsedMillis"] >= 0);

                Assert.Equal(CommandCompletionStatus.Faulted, result.Status);
                Assert.Equal(expectedResultValue, result.Value);
            }

            [Fact]
            public async Task ExecutionRejected_FailureModeThrow_Throws()
            {
                // When failure mode is Throw, rejections should result in an exception.

                var expectedException = new CircuitBreakerRejectedException();
                var command = new NoOpAsyncCommand();

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())).Throws(expectedException);

                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

                // We're testing the combination of OnFailure.Throw and the exceptions here.
                var exception = await Assert.ThrowsAsync<CircuitBreakerRejectedException>(() => invoker.InvokeAsync(command, OnFailure.Throw));

                mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOpAsync execute", "Rejected", It.IsAny<TimeSpan>()));
                Assert.Equal("test.NoOpAsync", exception.Data["Command"]);
                Assert.Equal(CommandCompletionStatus.Rejected, exception.Data["Status"]);
                Assert.Equal(GroupKey.Named("test"), exception.Data["Breaker"]);
                Assert.Equal(GroupKey.Named("test"), exception.Data["Bulkhead"]);
                Assert.Equal((int)command.DetermineTimeout().TotalMilliseconds, exception.Data["TimeoutMillis"]);
                Assert.True((double)exception.Data["ElapsedMillis"] >= 0);
            }

            [Fact]
            public async Task ExecutionRejected_FailureModeReturn_Returns()
            {
                // When failure mode is Return, rejections shouldn't throw, and should instead
                // return a result object with error information.

                // When a command fails and failure mode is Return, the value of the result will
                // be default(Type). Our test Command uses a bool, so we should expect a false.
                // Callers shouldn't be checking the result, anyway - if they're using the
                // Return failure mode, they should be checking for success first.
                const bool expectedResultValue = default(bool);

                var expectedException = new BulkheadRejectedException();
                var command = new NoOpAsyncCommand();

                var mockStats = new Mock<IStats>();
                var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
                mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkheadAsync(command, It.IsAny<CancellationToken>())).Throws(expectedException);

                var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

                // We're testing the combination of OnFailure.Return and the exceptions here.
                var result = await invoker.InvokeAsync(command, OnFailure.Return);

                mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOpAsync execute", "Rejected", It.IsAny<TimeSpan>()));

                Assert.NotNull(result.Exception);
                Assert.Equal(expectedException, result.Exception);
                Assert.Equal("test.NoOpAsync", result.Exception.Data["Command"]);
                Assert.Equal(CommandCompletionStatus.Rejected, result.Exception.Data["Status"]);
                Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Breaker"]);
                Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Bulkhead"]);
                Assert.Equal((int)command.DetermineTimeout().TotalMilliseconds, result.Exception.Data["TimeoutMillis"]);
                Assert.True((double)result.Exception.Data["ElapsedMillis"] >= 0);

                Assert.Equal(CommandCompletionStatus.Rejected, result.Status);
                Assert.Equal(expectedResultValue, result.Value);
            }
        }
    }

    public class Invoke : TestFixture
    {
        [Fact]
        public void WhenCommandAlreadyInvoked_FailureModeIsThrow_Throws()
        {
            // A command instance should only be invoked once. The failure mode
            // here shouldn't actually matter; there's a similar test below
            // that asserts the same throwing behavior here with a different
            // failure mode.

            // The command used here doesn't matter.
            var command = new NoOpCommand();
            var invoker = new CommandInvoker();
            var failureMode = OnFailure.Throw;

            // This first call shouldn't throw, but the second one should.
            invoker.Invoke(command, failureMode);

            // The timeout shouldn't matter. The invoked-once check should be
            // one of the first validations performed.
            Assert.Throws(typeof(InvalidOperationException), () => invoker.Invoke(command, failureMode));
        }

        [Fact]
        public void WhenCommandAlreadyInvoked_FailureModeIsReturn_StillThrows()
        {
            // Even if the failure mode isn't "Throw", we want to throw in
            // this situation. This is a bug on the calling side, since Command
            // instances shouldn't be reused. The exception thrown should help
            // the caller see that problem and fix it.

            // The command used here doesn't matter.
            var command = new NoOpCommand();
            var invoker = new CommandInvoker();
            var failureMode = OnFailure.Return;

            // This first call shouldn't throw, but the second one should.
            invoker.Invoke(command, failureMode);

            // The timeout shouldn't matter. The invoked-once check should be
            // one of the first validations performed.
            Assert.Throws(typeof(InvalidOperationException), () => invoker.Invoke(command, failureMode));
        }

        [Fact]
        public void ExecuteSuccessful_FailureModeThrow_ReturnsWrappedResult()
        {
            // Successful command execution should return a wrapped CommandResult.

            const bool expectedResultValue = true;
            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
            mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())).Returns(expectedResultValue);

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

            // We're testing OnFailure.Throws here. Mainly, it shouldn't throw if we're successful.
            var result = invoker.Invoke(command, OnFailure.Throw);

            mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())); // TODO test CancellationToken more accurately?
            mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOp execute", "RanToCompletion", It.IsAny<TimeSpan>()));
            Assert.Equal(CommandCompletionStatus.RanToCompletion, result.Status);
            Assert.Null(result.Exception);
            Assert.Equal(true, result.Value);
        }

        [Fact]
        public void ExecuteSuccessful_FailureModeReturn_ReturnsWrappedResult()
        {
            // Successful command execution should return a wrapped CommandResult.

            const bool expectedResultValue = true;
            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
            mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())).Returns(expectedResultValue);

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

            // We're testing OnFailure.Return here. The failure mode shouldn't have any bearing
            // on what happens during successful execution.
            var result = invoker.Invoke(command, OnFailure.Return);

            mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())); // TODO test CancellationToken more accurately?
            mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOp execute", "RanToCompletion", It.IsAny<TimeSpan>()));
            Assert.Equal(CommandCompletionStatus.RanToCompletion, result.Status);
            Assert.Null(result.Exception);
            Assert.Equal(true, result.Value);
        }

        [Fact]
        public void ExecutionCanceled_FailureModeThrow_Throws()
        {
            // When failure mode is Throw, cancellation should result in an exception. Note
            // that cancellation is different from timeouts (tested below). Timeouts are a
            // specific form of cancellation, and get logged/tracked as timeouts instead of
            // general "cancellations".

            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);
            var expiredToken = new CancellationToken(true);

            // We're testing the combination of OnFailure.Throw and the expired token here.
            var exception = Assert.Throws<OperationCanceledException>(() => invoker.Invoke(command, OnFailure.Throw, expiredToken));

            // We shouldn't have even attempted the execution since the token was expired upon
            // entering the method.
            mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>()), Times.Never);

            mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOp execute", "Canceled", It.IsAny<TimeSpan>()));
            Assert.Equal("test.NoOp", exception.Data["Command"]);
            Assert.Equal(CommandCompletionStatus.Canceled, exception.Data["Status"]);
            Assert.Equal(GroupKey.Named("test"), exception.Data["Breaker"]);
            Assert.Equal(GroupKey.Named("test"), exception.Data["Bulkhead"]);
            Assert.Equal("Token", exception.Data["TimeoutMillis"]);
            Assert.True((double)exception.Data["ElapsedMillis"] >= 0);
        }

        [Fact]
        public void ExecutionCanceled_FailureModeReturn_Returns()
        {
            // When failure mode is Return, cancellation shouldn't throw, and should instead
            // return a result object with error information. Note that cancellation is
            // different from timeouts (tested below). Timeouts are a specific form of
            // cancellation, and get logged/tracked as timeouts instead of general
            // "cancellations".

            // When a command fails and failure mode is Return, the value of the result will
            // be default(Type). Our test Command uses a bool, so we should expect a false.
            // Callers shouldn't be checking the result, anyway - if they're using the
            // Return failure mode, they should be checking for success first.
            const bool expectedResultValue = default(bool);
            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();

            // We're testing the combination of OnFailure.Return and the expired token here.
            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);
            var expiredToken = new CancellationToken(true);

            // Shouldn't throw.
            var result = invoker.Invoke(command, OnFailure.Return, expiredToken);

            // We shouldn't have even attempted the execution since the token was expired upon
            // entering the method.
            mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>()), Times.Never);

            mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOp execute", "Canceled", It.IsAny<TimeSpan>()));

            Assert.NotNull(result.Exception);
            Assert.True(result.Exception.GetType() == typeof(OperationCanceledException));
            Assert.Equal("test.NoOp", result.Exception.Data["Command"]);
            Assert.Equal(CommandCompletionStatus.Canceled, result.Exception.Data["Status"]);
            Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Breaker"]);
            Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Bulkhead"]);
            Assert.Equal("Token", result.Exception.Data["TimeoutMillis"]);
            Assert.True((double)result.Exception.Data["ElapsedMillis"] >= 0);

            Assert.Equal(CommandCompletionStatus.Canceled, result.Status);
            Assert.Equal(expectedResultValue, result.Value);
        }

        [Fact]
        public void TimeoutBeforeExecution_FailureModeThrow_Throws()
        {
            // When failure mode is Throw, timeouts should result in an exception. Timeouts
            // are a specific form of cancellation, and get logged/tracked as timeouts instead
            // of general "cancellations".

            var command = new SleepCommand(1);

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

            // We're testing the combination of OnFailure.Throw and the timeout here.
            var exception = Assert.Throws<OperationCanceledException>(() => invoker.Invoke(command, OnFailure.Throw, 0));

            // We shouldn't have even attempted the execution since the timeout expired by the
            // time we entered the method.
            mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>()), Times.Never);

            mockStats.Verify(m => m.Elapsed("mjolnir command test.Sleep execute", "TimedOut", It.IsAny<TimeSpan>()));
            Assert.Equal("test.Sleep", exception.Data["Command"]);
            Assert.Equal(CommandCompletionStatus.TimedOut, exception.Data["Status"]);
            Assert.Equal(GroupKey.Named("test"), exception.Data["Breaker"]);
            Assert.Equal(GroupKey.Named("test"), exception.Data["Bulkhead"]);
            Assert.Equal(0, exception.Data["TimeoutMillis"]);
            Assert.True((double)exception.Data["ElapsedMillis"] >= 0);
        }

        [Fact]
        public void TimeoutBeforeExecution_FailureModeReturn_Returns()
        {
            // When failure mode is Return, cancellation shouldn't throw, and should instead
            // return a result object with error information. Timeouts are a specific form of
            // cancellation, and get logged/tracked as timeouts instead of general
            // "cancellations".

            // When a command fails and failure mode is Return, the value of the result will
            // be default(Type). Our test Command uses a bool, so we should expect a false.
            // Callers shouldn't be checking the result, anyway - if they're using the
            // Return failure mode, they should be checking for success first.
            const bool expectedResultValue = default(bool);

            var command = new SleepCommand(1);

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

            // We're testing the combination of OnFailure.Return and the timeout here.
            var result = invoker.Invoke(command, OnFailure.Return, 0);

            // We shouldn't have even attempted the execution since the timeout expired by the
            // time we entered the method.
            mockBulkheadInvoker.Verify(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>()), Times.Never);

            mockStats.Verify(m => m.Elapsed("mjolnir command test.Sleep execute", "TimedOut", It.IsAny<TimeSpan>()));

            Assert.NotNull(result.Exception);
            Assert.True(result.Exception.GetType() == typeof(OperationCanceledException));
            Assert.Equal("test.Sleep", result.Exception.Data["Command"]);
            Assert.Equal(CommandCompletionStatus.TimedOut, result.Exception.Data["Status"]);
            Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Breaker"]);
            Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Bulkhead"]);
            Assert.Equal(0, result.Exception.Data["TimeoutMillis"]);
            Assert.True((double)result.Exception.Data["ElapsedMillis"] >= 0);

            Assert.Equal(CommandCompletionStatus.TimedOut, result.Status);
            Assert.Equal(expectedResultValue, result.Value);
        }

        [Fact]
        public void CancellationIgnored_InvokesRegardlessOfInvocationToken()
        {
            // When doing local development, timeouts often get hit after sitting on a
            // breakpoint for too long. An optional configuration can be set to ignore
            // cancellation and work around that. If the config is set, all cancellation
            // (regardless of source) is ignored.

            var command = new SleepCommand(1);
            var expiredToken = new CancellationToken(true);

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
            mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())).Returns(true);

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object, new TransientConfigurableValue<bool>(true));

            // Even though we pass a token, the config value on the invoker should prevent the
            // token from being used/checked. This shouldn't throw.
            var result = invoker.Invoke(command, OnFailure.Throw, expiredToken);

            Assert.Equal(CommandCompletionStatus.RanToCompletion, result.Status);
        }

        [Fact]
        public void CancellationIgnored_InvokesRegardlessOfInvocationTimeout()
        {
            // When doing local development, timeouts often get hit after sitting on a
            // breakpoint for too long. An optional configuration can be set to ignore
            // cancellation and work around that. If the config is set, all cancellation
            // (regardless of source) is ignored.

            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
            mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())).Returns(true);

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object, new TransientConfigurableValue<bool>(true));

            // Even though we pass a timeout, the config value on the invoker should prevent the
            // timeout from being used/checked. This shouldn't throw.
            var result = invoker.Invoke(command, OnFailure.Throw, 0);

            Assert.Equal(CommandCompletionStatus.RanToCompletion, result.Status);
        }

        [Fact]
        public void CancellationIgnoredAndExecutionFaulted_MarksTimeoutIgnoredOnExceptionData()
        {
            // If a command faults and cancellation is disabled, we should still log the
            // timeout we would have used on the exception. This is a fairly inconsequential
            // test, but we might as well make sure what we're logging is accurate.

            var expectedException = new ExpectedTestException("Expected");
            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
            mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())).Throws(expectedException);

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object, new TransientConfigurableValue<bool>(true));

            // We're testing the combination of OnFailure.Throw and the exceptions here.
            var exception = Assert.Throws<ExpectedTestException>(() => invoker.Invoke(command, OnFailure.Throw));

            Assert.NotNull(exception);
            Assert.Equal("Ignored", exception.Data["TimeoutMillis"]);
        }

        [Fact]
        public void ExecutionFaulted_FailureModeThrow_Throws()
        {
            // When failure mode is Throw, faults should result in an exception.

            var expectedException = new ExpectedTestException("Expected");
            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
            mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())).Throws(expectedException);

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

            // We're testing the combination of OnFailure.Throw and the exceptions here.
            var exception = Assert.Throws<ExpectedTestException>(() => invoker.Invoke(command, OnFailure.Throw));

            mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOp execute", "Faulted", It.IsAny<TimeSpan>()));
            Assert.Equal("test.NoOp", exception.Data["Command"]);
            Assert.Equal(CommandCompletionStatus.Faulted, exception.Data["Status"]);
            Assert.Equal(GroupKey.Named("test"), exception.Data["Breaker"]);
            Assert.Equal(GroupKey.Named("test"), exception.Data["Bulkhead"]);
            Assert.Equal((int)command.DetermineTimeout().TotalMilliseconds, exception.Data["TimeoutMillis"]);
            Assert.True((double)exception.Data["ElapsedMillis"] >= 0);
        }

        [Fact]
        public void ExecutionFaulted_FailureModeReturn_Returns()
        {
            // When failure mode is Return, faults shouldn't throw, and should instead
            // return a result object with error information.

            // When a command fails and failure mode is Return, the value of the result will
            // be default(Type). Our test Command uses a bool, so we should expect a false.
            // Callers shouldn't be checking the result, anyway - if they're using the
            // Return failure mode, they should be checking for success first.
            const bool expectedResultValue = default(bool);

            var expectedException = new ExpectedTestException("Expected");
            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
            mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())).Throws(expectedException);

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

            // We're testing the combination of OnFailure.Return and the exceptions here.
            var result = invoker.Invoke(command, OnFailure.Return);

            mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOp execute", "Faulted", It.IsAny<TimeSpan>()));

            Assert.NotNull(result.Exception);
            Assert.Equal(expectedException, result.Exception);
            Assert.Equal("test.NoOp", result.Exception.Data["Command"]);
            Assert.Equal(CommandCompletionStatus.Faulted, result.Exception.Data["Status"]);
            Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Breaker"]);
            Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Bulkhead"]);
            Assert.Equal((int)command.DetermineTimeout().TotalMilliseconds, result.Exception.Data["TimeoutMillis"]);
            Assert.True((double)result.Exception.Data["ElapsedMillis"] >= 0);

            Assert.Equal(CommandCompletionStatus.Faulted, result.Status);
            Assert.Equal(expectedResultValue, result.Value);
        }

        [Fact]
        public void ExecutionRejected_FailureModeThrow_Throws()
        {
            // When failure mode is Throw, rejections should result in an exception.

            var expectedException = new CircuitBreakerRejectedException();
            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
            mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())).Throws(expectedException);

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

            // We're testing the combination of OnFailure.Throw and the exceptions here.
            var exception = Assert.Throws<CircuitBreakerRejectedException>(() => invoker.Invoke(command, OnFailure.Throw));

            mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOp execute", "Rejected", It.IsAny<TimeSpan>()));
            Assert.Equal("test.NoOp", exception.Data["Command"]);
            Assert.Equal(CommandCompletionStatus.Rejected, exception.Data["Status"]);
            Assert.Equal(GroupKey.Named("test"), exception.Data["Breaker"]);
            Assert.Equal(GroupKey.Named("test"), exception.Data["Bulkhead"]);
            Assert.Equal((int)command.DetermineTimeout().TotalMilliseconds, exception.Data["TimeoutMillis"]);
            Assert.True((double)exception.Data["ElapsedMillis"] >= 0);
        }

        [Fact]
        public void ExecutionRejected_FailureModeReturn_Returns()
        {
            // When failure mode is Return, rejections shouldn't throw, and should instead
            // return a result object with error information.

            // When a command fails and failure mode is Return, the value of the result will
            // be default(Type). Our test Command uses a bool, so we should expect a false.
            // Callers shouldn't be checking the result, anyway - if they're using the
            // Return failure mode, they should be checking for success first.
            const bool expectedResultValue = default(bool);

            var expectedException = new BulkheadRejectedException();
            var command = new NoOpCommand();

            var mockStats = new Mock<IStats>();
            var mockBulkheadInvoker = new Mock<IBulkheadInvoker>();
            mockBulkheadInvoker.Setup(m => m.ExecuteWithBulkhead(command, It.IsAny<CancellationToken>())).Throws(expectedException);

            var invoker = new CommandInvoker(mockStats.Object, mockBulkheadInvoker.Object);

            // We're testing the combination of OnFailure.Return and the exceptions here.
            var result = invoker.Invoke(command, OnFailure.Return);

            mockStats.Verify(m => m.Elapsed("mjolnir command test.NoOp execute", "Rejected", It.IsAny<TimeSpan>()));

            Assert.NotNull(result.Exception);
            Assert.Equal(expectedException, result.Exception);
            Assert.Equal("test.NoOp", result.Exception.Data["Command"]);
            Assert.Equal(CommandCompletionStatus.Rejected, result.Exception.Data["Status"]);
            Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Breaker"]);
            Assert.Equal(GroupKey.Named("test"), result.Exception.Data["Bulkhead"]);
            Assert.Equal((int)command.DetermineTimeout().TotalMilliseconds, result.Exception.Data["TimeoutMillis"]);
            Assert.True((double)result.Exception.Data["ElapsedMillis"] >= 0);

            Assert.Equal(CommandCompletionStatus.Rejected, result.Status);
            Assert.Equal(expectedResultValue, result.Value);
        }
    }

    // Async command that does nothing, and doesn't actually go async - it wraps a synchronous
    // result and returns. Use this when you don't care what the Command actually does.
    internal class NoOpAsyncCommand : AsyncCommand<bool>
    {
        // High default to avoid unplanned timeouts. Tests should specify a timeout if they want
        // to test timeout behavior.
        public NoOpAsyncCommand() : this(TimeSpan.FromHours(1)) { }
        public NoOpAsyncCommand(TimeSpan timeout) : base("test", "test", timeout) { }
        
        protected internal override Task<bool> ExecuteAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }
    }

    internal class DelayAsyncCommand : AsyncCommand<bool>
    {
        private readonly int _millis;

        // High default to avoid unplanned timeouts. Tests should specify a timeout if they want
        // to test timeout behavior.
        public DelayAsyncCommand(int millis) : base("test", "test", TimeSpan.FromHours(1))
        {
            _millis = millis;
        }

        protected internal override async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(_millis, cancellationToken);
            return true;
        }
    }

    // Synchronous ommand that does nothing, use this when you don't care what the Command
    // actually does.
    internal class NoOpCommand : SyncCommand<bool>
    {
        // High default to avoid unplanned timeouts. Tests should specify a timeout if they want
        // to test timeout behavior.
        public NoOpCommand() : this(TimeSpan.FromHours(1)) { }
        public NoOpCommand(TimeSpan timeout) : base("test", "test", timeout) { }

        protected internal override bool Execute(CancellationToken cancellationToken)
        {
            return true;
        }
    }

    internal class SleepCommand : SyncCommand<bool>
    {
        private readonly int _millis;

        // High default to avoid unplanned timeouts. Tests should specify a timeout if they want
        // to test timeout behavior.
        public SleepCommand(int millis) : base ("test", "test", TimeSpan.FromHours(1))
        {
            _millis = millis;
        }

        protected internal override bool Execute(CancellationToken cancellationToken)
        {
            Thread.Sleep(_millis);
            return true;
        }
    }
}