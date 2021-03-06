﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Xunit.Abstractions;

namespace Xunit.Runner.VisualStudio.TestAdapter
{
    [FileExtension(".appx")]
    [FileExtension(".dll")]
    [FileExtension(".exe")]
    [DefaultExecutorUri(Constants.ExecutorUri)]
    [ExtensionUri(Constants.ExecutorUri)]
    public class VsTestRunner : ITestDiscoverer, ITestExecutor
    {
        public static TestProperty SerializedTestCaseProperty = GetTestProperty();

        static readonly HashSet<string> platformAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft.visualstudio.testplatform.unittestframework.dll", 
            "microsoft.visualstudio.testplatform.core.dll", 
            "microsoft.visualstudio.testplatform.testexecutor.core.dll", 
            "microsoft.visualstudio.testplatform.extensions.msappcontaineradapter.dll", 
            "microsoft.visualstudio.testplatform.objectmodel.dll", 
            "microsoft.visualstudio.testplatform.utilities.dll", 
            "vstest.executionengine.appcontainer.exe", 
            "vstest.executionengine.appcontainer.x86.exe",
            "xunit.execution.desktop.dll",
            "xunit.execution.win8.dll",
            "xunit.execution.universal.dll",
            "xunit.runner.utility.desktop.dll",
            "xunit.runner.utility.win8.dll",
            "xunit.runner.utility.universal.dll",
            "xunit.runner.visualstudio.testadapter.dll",
            "xunit.core.dll",
            "xunit.assert.dll"
        };

        bool cancelled;

        public void Cancel()
        {
            cancelled = true;
        }

        public void DiscoverTests(IEnumerable<string> sources, IDiscoveryContext discoveryContext, IMessageLogger logger, ITestCaseDiscoverySink discoverySink)
        {
            Guard.ArgumentNotNull("sources", sources);
            Guard.ArgumentNotNull("logger", logger);
            Guard.ArgumentNotNull("discoverySink", discoverySink);
            Guard.ArgumentValid("sources", "AppX not supported for discovery", !ContainsAppX(sources));

            DiscoverTests(
                sources,
                logger,
                (source, discoverer, discoveryOptions) => new VsDiscoveryVisitor(source, discoverer, logger, discoverySink, discoveryOptions, () => cancelled)
            );
        }

        void DiscoverTests<TVisitor>(IEnumerable<string> sources,
                                     IMessageLogger logger,
                                     Func<string, ITestFrameworkDiscoverer, ITestFrameworkDiscoveryOptions, TVisitor> visitorFactory,
                                     Action<string, ITestFrameworkDiscoverer, ITestFrameworkDiscoveryOptions, TVisitor> visitComplete = null,
                                     Stopwatch stopwatch = null)
            where TVisitor : IVsDiscoveryVisitor, IDisposable
        {
            if (stopwatch == null)
                stopwatch = Stopwatch.StartNew();

            try
            {
                RemotingUtility.CleanUpRegisteredChannels();

                using (AssemblyHelper.SubscribeResolve())
                {
                    foreach (var assemblyFileName in sources)
                    {
                        var configuration = ConfigReader.Load(assemblyFileName);
                        var fileName = Path.GetFileNameWithoutExtension(assemblyFileName);

                        try
                        {
                            if (cancelled)
                                break;

                            if (!IsXunitTestAssembly(assemblyFileName))
                            {
                                if (configuration.DiagnosticMessagesOrDefault)
                                    logger.SendMessage(TestMessageLevel.Informational,
                                                       String.Format("[xUnit.net {0}] Skipping: {1} (no reference to xUnit.net)", stopwatch.Elapsed, fileName));
                            }
                            else
                            {
                                var diagnosticMessageVisitor = new DiagnosticMessageVisitor(logger, fileName, configuration.DiagnosticMessagesOrDefault);

                                using (var framework = new XunitFrontController(assemblyFileName, configFileName: null, shadowCopy: true, diagnosticMessageSink: diagnosticMessageVisitor))
                                {
                                    var targetFramework = framework.TargetFramework;
                                    if (targetFramework.StartsWith("MonoTouch", StringComparison.OrdinalIgnoreCase) ||
                                        targetFramework.StartsWith("MonoAndroid", StringComparison.OrdinalIgnoreCase) ||
                                        targetFramework.StartsWith("Xamarin.iOS", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (configuration.DiagnosticMessagesOrDefault)
                                            logger.SendMessage(TestMessageLevel.Informational, String.Format("[xUnit.net {0}] Skipping: {1} (unsupported target framework '{2}')", stopwatch.Elapsed, fileName, targetFramework));
                                    }
                                    else
                                    {
                                        var discoveryOptions = TestFrameworkOptions.ForDiscovery(configuration);

                                        if (configuration.DiagnosticMessagesOrDefault)
                                            logger.SendMessage(TestMessageLevel.Informational,
                                                               String.Format("[xUnit.net {0}] Discovery starting: {1} (name display = {2})", stopwatch.Elapsed, fileName, discoveryOptions.GetMethodDisplayOrDefault()));

                                        using (var visitor = visitorFactory(assemblyFileName, framework, discoveryOptions))
                                        {
                                            framework.Find(includeSourceInformation: true, messageSink: visitor, discoveryOptions: discoveryOptions);
                                            var totalTests = visitor.Finish();

                                            if (visitComplete != null)
                                                visitComplete(assemblyFileName, framework, discoveryOptions, visitor);

                                            if (configuration.DiagnosticMessagesOrDefault)
                                                logger.SendMessage(TestMessageLevel.Informational,
                                                                   String.Format("[xUnit.net {0}] Discovery finished: {1} ({2} tests)", stopwatch.Elapsed, fileName, totalTests));
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            var ex = e.Unwrap();
                            var fileNotFound = ex as FileNotFoundException;
#if !WINDOWS_PHONE_APP && !WINDOWS_PHONE
                            var fileLoad = ex as FileLoadException;
#endif
                            if (fileNotFound != null)
                                logger.SendMessage(TestMessageLevel.Informational,
                                                   String.Format("[xUnit.net {0}] Skipping: {1} (could not find dependent assembly '{2}')", stopwatch.Elapsed, fileName, Path.GetFileNameWithoutExtension(fileNotFound.FileName)));
#if !WINDOWS_PHONE_APP && !WINDOWS_PHONE
                            else if (fileLoad != null)
                                logger.SendMessage(TestMessageLevel.Informational,
                                                   String.Format("[xUnit.net {0}] Skipping: {1} (could not find dependent assembly '{2}')", stopwatch.Elapsed, fileName, Path.GetFileNameWithoutExtension(fileLoad.FileName)));
#endif
                            else
                                logger.SendMessage(TestMessageLevel.Error,
                                                   String.Format("[xUnit.net {0}] Exception discovering tests from {1}: {2}", stopwatch.Elapsed, fileName, ex));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.SendMessage(TestMessageLevel.Error,
                                   String.Format("[xUnit.net {0}] Exception discovering tests: {1}", stopwatch.Elapsed, e.Unwrap()));
            }

            stopwatch.Stop();
        }

        static TestProperty GetTestProperty()
        {
            return TestProperty.Register("XunitTestCase", "xUnit.net Test Case", typeof(string), typeof(VsTestRunner));
        }

        List<AssemblyRunInfo> GetTests(IEnumerable<string> sources, IMessageLogger logger, Stopwatch stopwatch)
        {
#if WIN8_STORE
            // For store apps, the files are copied to the AppX dir, we need to load it from there
            sources = sources.Select(s => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), Path.GetFileName(s)));
#elif WINDOWS_PHONE_APP
            sources = sources.Select(s => Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, Path.GetFileName(s))); ;
#endif

            var result = new List<AssemblyRunInfo>();

            DiscoverTests(
                sources,
                logger,
                (source, discoverer, discoveryOptions) => new VsExecutionDiscoveryVisitor(),
                (source, discoverer, discoveryOptions, visitor) =>
                    result.Add(
                        new AssemblyRunInfo
                        {
                            AssemblyFileName = source,
                            Configuration = ConfigReader.Load(source),
                            TestCases = visitor.TestCases
                                   .GroupBy(tc => String.Format("{0}.{1}", tc.TestMethod.TestClass.Class.Name, tc.TestMethod.Method.Name))
                                   .SelectMany(group => group.Select(testCase => VsDiscoveryVisitor.CreateVsTestCase(source,
                                                                                                                     discoverer,
                                                                                                                     testCase,
                                                                                                                     forceUniqueNames: group.Count() > 1,
                                                                                                                     logger: logger,
                                                                                                                     logDiscovery: discoveryOptions.GetDiagnosticMessagesOrDefault()))
                                                             .Where(vsTestCase => vsTestCase != null))
                                   .ToList()
                        }),
                stopwatch
            );

            return result;
        }

        static bool IsXunitTestAssembly(string assemblyFileName)
        {
            // Don't try to load ourselves (or any test framework assemblies), since we fail (issue #47 in xunit/xunit).
            if (platformAssemblies.Contains(Path.GetFileName(assemblyFileName)))
                return false;

            var xunitPath = Path.Combine(Path.GetDirectoryName(assemblyFileName), "xunit.dll");
            var xunitExecutionPath = Path.Combine(Path.GetDirectoryName(assemblyFileName), ExecutionHelper.AssemblyName);
            return File.Exists(xunitPath) || File.Exists(xunitExecutionPath);
        }

        public void RunTests(IEnumerable<string> sources, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            Guard.ArgumentNotNull("sources", sources);

            // In this case, we need to go thru the files manually
            if (ContainsAppX(sources))
            {
#if WINDOWS_PHONE_APP
                var sourcePath = Windows.ApplicationModel.Package.Current.InstalledLocation.Path;
#else
                var sourcePath = Environment.CurrentDirectory;
#endif
                sources = Directory.GetFiles(sourcePath, "*.dll")
                                   .Where(file => !platformAssemblies.Contains(Path.GetFileName(file)))
                                   .ToList();
            }

            var stopwatch = Stopwatch.StartNew();
            RunTests(runContext, frameworkHandle, stopwatch, () => GetTests(sources, frameworkHandle, stopwatch));
            stopwatch.Stop();
        }

        public void RunTests(IEnumerable<TestCase> tests, IRunContext runContext, IFrameworkHandle frameworkHandle)
        {
            Guard.ArgumentNotNull("tests", tests);
            Guard.ArgumentValid("tests", "AppX not supported in this overload", !ContainsAppX(tests.Select(t => t.Source)));

            var stopwatch = Stopwatch.StartNew();
            RunTests(runContext, frameworkHandle, stopwatch, () => tests.GroupBy(testCase => testCase.Source)
                                                                        .Select(group => new AssemblyRunInfo { AssemblyFileName = group.Key, Configuration = ConfigReader.Load(group.Key), TestCases = group })
                                                                        .ToList());
            stopwatch.Stop();
        }

        void RunTests(IRunContext runContext, IFrameworkHandle frameworkHandle, Stopwatch stopwatch, Func<List<AssemblyRunInfo>> testCaseAccessor)
        {
            Guard.ArgumentNotNull("runContext", runContext);
            Guard.ArgumentNotNull("frameworkHandle", frameworkHandle);

            var toDispose = new List<IDisposable>();

            try
            {
                RemotingUtility.CleanUpRegisteredChannels();

                cancelled = false;

                var assemblies = testCaseAccessor();
                var parallelizeAssemblies = assemblies.All(runInfo => runInfo.Configuration.ParallelizeAssemblyOrDefault);

                using (AssemblyHelper.SubscribeResolve())
                    if (parallelizeAssemblies)
                        assemblies
                            .Select(runInfo => RunTestsInAssemblyAsync(frameworkHandle, toDispose, runInfo, stopwatch))
                            .ToList()
                            .ForEach(@event => @event.WaitOne());
                    else
                        assemblies
                            .ForEach(runInfo => RunTestsInAssembly(frameworkHandle, toDispose, runInfo, stopwatch));
            }
            finally
            {
                toDispose.ForEach(disposable => disposable.Dispose());
            }
        }

        void RunTestsInAssembly(IFrameworkHandle frameworkHandle,
                                List<IDisposable> toDispose,
                                AssemblyRunInfo runInfo,
                                Stopwatch stopwatch)
        {
            if (cancelled)
                return;

            var assemblyFileName = runInfo.AssemblyFileName;
            var assemblyDisplayName = Path.GetFileNameWithoutExtension(assemblyFileName);

            if (runInfo.Configuration.DiagnosticMessagesOrDefault)
                lock (stopwatch)
                    frameworkHandle.SendMessage(TestMessageLevel.Informational, String.Format("[xUnit.net {0}] Execution starting: {1} (method display = {2}, parallel test collections = {3}, max threads = {4})",
                                                                                              stopwatch.Elapsed,
                                                                                              assemblyDisplayName,
                                                                                              runInfo.Configuration.MethodDisplayOrDefault,
                                                                                              runInfo.Configuration.ParallelizeTestCollectionsOrDefault,
                                                                                              runInfo.Configuration.MaxParallelThreadsOrDefault));

#if WIN8_STORE
            // For store apps, the files are copied to the AppX dir, we need to load it from there
            assemblyFileName = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), Path.GetFileName(assemblyFileName));
#elif WINDOWS_PHONE_APP
            // For WPA Apps, use the package location
            assemblyFileName = Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, Path.GetFileName(assemblyFileName));
#endif

            var diagnosticMessageVisitor = new DiagnosticMessageVisitor(frameworkHandle, assemblyDisplayName, runInfo.Configuration.DiagnosticMessagesOrDefault);
            var controller = new XunitFrontController(assemblyFileName, configFileName: null, shadowCopy: true, diagnosticMessageSink: diagnosticMessageVisitor);

            lock (toDispose)
                toDispose.Add(controller);

            var xunitTestCases = runInfo.TestCases.ToDictionary(tc => controller.Deserialize(tc.GetPropertyValue<string>(SerializedTestCaseProperty, null)));
            var executionOptions = TestFrameworkOptions.ForExecution(runInfo.Configuration);

            using (var executionVisitor = new VsExecutionVisitor(frameworkHandle, xunitTestCases, executionOptions, () => cancelled))
            {
                controller.RunTests(xunitTestCases.Keys.ToList(), executionVisitor, executionOptions);
                executionVisitor.Finished.WaitOne();
            }

            if (runInfo.Configuration.DiagnosticMessagesOrDefault)
                lock (stopwatch)
                    frameworkHandle.SendMessage(TestMessageLevel.Informational, String.Format("[xUnit.net {0}] Execution finished: {1}", stopwatch.Elapsed, assemblyDisplayName));
        }

        ManualResetEvent RunTestsInAssemblyAsync(IFrameworkHandle frameworkHandle,
                                                 List<IDisposable> toDispose,
                                                 AssemblyRunInfo runInfo,
                                                 Stopwatch stopwatch)
        {
            var @event = new ManualResetEvent(initialState: false);
            Action handler = () =>
            {
                try
                {
                    RunTestsInAssembly(frameworkHandle, toDispose, runInfo, stopwatch);
                }
                finally
                {
                    @event.Set();
                }
            };

#if WINDOWS_PHONE_APP
            var fireAndForget = Windows.System.Threading.ThreadPool.RunAsync(_ => handler());
#else
            ThreadPool.QueueUserWorkItem(_ => handler());
#endif

            return @event;
        }


        private static bool ContainsAppX(IEnumerable<string> sources)
        {
            return sources.Any(s => string.Compare(Path.GetExtension(s), ".appx", StringComparison.OrdinalIgnoreCase) == 0);
        }

        class Grouping<TKey, TElement> : IGrouping<TKey, TElement>
        {
            readonly IEnumerable<TElement> elements;

            public Grouping(TKey key, IEnumerable<TElement> elements)
            {
                Key = key;
                this.elements = elements;
            }

            public TKey Key { get; private set; }

            public IEnumerator<TElement> GetEnumerator()
            {
                return elements.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return elements.GetEnumerator();
            }
        }
    }
}
