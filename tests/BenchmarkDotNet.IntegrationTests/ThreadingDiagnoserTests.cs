#if !NETFRAMEWORK
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Tests.Loggers;
using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.CoreRt;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BenchmarkDotNet.Portability;
using Xunit;
using Xunit.Abstractions;

namespace BenchmarkDotNet.IntegrationTests
{
    public class ThreadingDiagnoserTests
    {
        private readonly ITestOutputHelper output;

        public ThreadingDiagnoserTests(ITestOutputHelper outputHelper) => output = outputHelper;

        public static IEnumerable<object[]> GetToolchains()
        {
            yield return new object[] { Job.Default.GetToolchain() };

            // Currently, CoreRtToolchain.Core50 produces the following error on Unix without installed clang:
            //   Standard error:
            //   ~/.nuget/packages/microsoft.dotnet.ilcompiler/6.0.0-preview.5.21269.1/build/Microsoft.NETCore.Native.Unix.props(99,5):
            //   error : Platform linker ('clang') not found. Try installing clang or the appropriate package for your platform to resolve the problem.
            if (RuntimeInformation.IsWindows())
                yield return new object[] { CoreRtToolchain.Core50 };

            // TODO: Support InProcessEmitToolchain.Instance
            // yield return new object[] { InProcessEmitToolchain.Instance };
        }

        [Theory, MemberData(nameof(GetToolchains))]
        public void CompletedWorkItemCountIsAccurate(IToolchain toolchain)
        {
            var config = CreateConfig(toolchain);

            var summary = BenchmarkRunner.Run<CompletedWorkItemCount>(config);

            AssertStats(summary, new Dictionary<string, (string metricName, double expectedValue)>
            {
                { nameof(CompletedWorkItemCount.DoNothing), ("CompletedWorkItemCount", 0.0) },
                { nameof(CompletedWorkItemCount.CompleteOneWorkItem), ("CompletedWorkItemCount", 1.0) }
            });
        }

        public class CompletedWorkItemCount
        {
            [Benchmark]
            public void CompleteOneWorkItem()
            {
                ManualResetEvent done = new ManualResetEvent(false);
                ThreadPool.QueueUserWorkItem(m => (m as ManualResetEvent).Set(), done);
                done.WaitOne();
            }

            [Benchmark]
            public void DoNothing() { }
        }

        [Theory, MemberData(nameof(GetToolchains))]
        public void LockContentionCountIsAccurate(IToolchain toolchain)
        {
            var config = CreateConfig(toolchain);

            var summary = BenchmarkRunner.Run<LockContentionCount>(config);

            AssertStats(summary, new Dictionary<string, (string metricName, double expectedValue)>
            {
                { nameof(LockContentionCount.DoNothing), ("LockContentionCount", 0.0) },
                { nameof(LockContentionCount.RunIntoLockContention), ("LockContentionCount", 1.0) }
            });
        }

        public class LockContentionCount
        {
            private readonly object guard = new object();

            private ManualResetEvent lockTaken;
            private ManualResetEvent failedToAcquire;

            [Benchmark]
            public void DoNothing() { }

            [Benchmark]
            public void RunIntoLockContention()
            {
                lockTaken = new ManualResetEvent(false);
                failedToAcquire = new ManualResetEvent(false);

                Thread first = new Thread(FirstThread);
                Thread second = new Thread(SecondThread);

                first.Start();
                second.Start();

                second.Join();
                first.Join();
            }

            private void FirstThread()
            {
                Monitor.Enter(guard);
                lockTaken.Set();

                failedToAcquire.WaitOne();
                Monitor.Exit(guard);
            }

            private void SecondThread()
            {
                lockTaken.WaitOne();

                bool taken = Monitor.TryEnter(guard, TimeSpan.FromMilliseconds(10));

                if (taken)
                {
                    throw new InvalidOperationException("Impossible!");
                }

                failedToAcquire.Set();
            }
        }

        private IConfig CreateConfig(IToolchain toolchain)
            => ManualConfig.CreateEmpty()
                .AddJob(Job.ShortRun
                    .WithEvaluateOverhead(false) // no need to run idle for this test
                    .WithWarmupCount(0) // don't run warmup to save some time for our CI runs
                    .WithIterationCount(1) // single iteration is enough for us
                    .WithGcForce(false)
                    .WithToolchain(toolchain))
                .AddColumnProvider(DefaultColumnProviders.Instance)
                .AddDiagnoser(ThreadingDiagnoser.Default)
                .AddLogger(toolchain.IsInProcess
                    ? ConsoleLogger.Default
                    : new OutputLogger(output)); // we can't use OutputLogger for the InProcess toolchains because it allocates memory on the same thread

        private void AssertStats(Summary summary, Dictionary<string, (string metricName, double expectedValue)> assertions)
        {
            foreach (var assertion in assertions)
            {
                var selectedReport = summary.Reports.Single(report => report.BenchmarkCase.DisplayInfo.Contains(assertion.Key));

                var metric = selectedReport.Metrics.Single(m => m.Key == assertion.Value.metricName);

                // precision is set to 3 because CoreCLR might schedule some work item on it's own and hence affect the results..
                Assert.Equal(assertion.Value.expectedValue, metric.Value.Value, precision: 3);
            }
        }
    }
}
#endif