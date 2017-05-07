// ***********************************************************************
// Copyright (c) 2014 Charlie Poole
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NUnit.Framework.Constraints;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal.Execution;

#if !PORTABLE && !NETSTANDARD1_6
using System.Runtime.Remoting.Messaging;
using System.Security;
using System.Security.Principal;
using NUnit.Compatibility;
#endif

namespace NUnit.Framework.Internal
{
    /// <summary>
    /// Helper class used to save and restore certain static or
    /// singleton settings in the environment that affect tests
    /// or which might be changed by the user tests.
    /// </summary>
    public class TestExecutionContext
#if !PORTABLE && !NETSTANDARD1_6
        : LongLivedMarshalByRefObject, ILogicalThreadAffinative
#endif
    {
        // NOTE: Be very careful when modifying this class. It uses
        // conditional compilation extensively and you must give
        // thought to whether any new features will be supported
        // on each platform. In particular, instance fields,
        // properties, initialization and restoration must all
        // use the same conditions for each feature.

        #region Instance Fields

        /// <summary>
        /// Link to a prior saved context
        /// </summary>
        private TestExecutionContext _priorContext;

        /// <summary>
        /// Indicates that a stop has been requested
        /// </summary>
        private TestExecutionStatus _executionStatus;

        /// <summary>
        /// The event listener currently receiving notifications
        /// </summary>
        private ITestListener _listener = TestListener.NULL;

        /// <summary>
        /// The number of assertions for the current test
        /// </summary>
        private int _assertCount;

        private Randomizer _randomGenerator;

        /// <summary>
        /// The current culture
        /// </summary>
        private CultureInfo _currentCulture;

        /// <summary>
        /// The current UI culture
        /// </summary>
        private CultureInfo _currentUICulture;

        /// <summary>
        /// The current test result
        /// </summary>
        private TestResult _currentResult;

        /// <summary>
        /// Files attached to the current test context
        /// </summary>
        private readonly IList<TestAttachment> _testAttachments = new List<TestAttachment>();

#if !PORTABLE && !NETSTANDARD1_6
        /// <summary>
        /// The current Principal.
        /// </summary>
        private IPrincipal _currentPrincipal;
#endif

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="TestExecutionContext"/> class.
        /// </summary>
        public TestExecutionContext()
        {
            _priorContext = null;
            TestCaseTimeout = 0;
            UpstreamActions = new List<ITestAction>();

            _currentCulture = CultureInfo.CurrentCulture;
            _currentUICulture = CultureInfo.CurrentUICulture;

#if !PORTABLE && !NETSTANDARD1_6
            _currentPrincipal = Thread.CurrentPrincipal;
#endif

            CurrentValueFormatter = (val) => MsgUtils.DefaultValueFormatter(val);
            IsSingleThreaded = false;
            DefaultFloatingPointTolerance = Tolerance.Default;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TestExecutionContext"/> class.
        /// </summary>
        /// <param name="other">An existing instance of TestExecutionContext.</param>
        public TestExecutionContext(TestExecutionContext other)
        {
            _priorContext = other;

            CurrentTest = other.CurrentTest;
            CurrentResult = other.CurrentResult;
            TestObject = other.TestObject;
            _listener = other._listener;
            StopOnError = other.StopOnError;
            TestCaseTimeout = other.TestCaseTimeout;
            _testAttachments = new List<TestAttachment>(other._testAttachments);
            UpstreamActions = new List<ITestAction>(other.UpstreamActions);

            _currentCulture = other.CurrentCulture;
            _currentUICulture = other.CurrentUICulture;

            DefaultFloatingPointTolerance = other.DefaultFloatingPointTolerance;

#if !PORTABLE && !NETSTANDARD1_6
            _currentPrincipal = other.CurrentPrincipal;
#endif

            CurrentValueFormatter = other.CurrentValueFormatter;

            Dispatcher = other.Dispatcher;
            ParallelScope = other.ParallelScope;
            IsSingleThreaded = other.IsSingleThreaded;
        }

        #endregion

        #region CurrentContext Instance

        // NOTE: We use different implementations for various platforms.

#if NETSTANDARD1_6
        private static readonly AsyncLocal<TestExecutionContext> _currentContext = new AsyncLocal<TestExecutionContext>();
        /// <summary>
        /// Gets and sets the current context.
        /// </summary>
        public static TestExecutionContext CurrentContext
        {
            get
            {
                return _currentContext.Value;
            }
            private set
            {
                _currentContext.Value = value;
            }
        }
#elif PORTABLE
        // The portable build only supports a single thread of
        // execution, so we can use a simple static field to
        // hold the current TestExecutionContext.
        private static TestExecutionContext _currentContext;

        /// <summary>
        /// Gets and sets the current context.
        /// </summary>
        public static TestExecutionContext CurrentContext
        {
            get { return _currentContext; }
            private set { _currentContext = value; }
        }
#else
        // In all other builds, we use the CallContext
        private static readonly string CONTEXT_KEY = "NUnit.Framework.TestContext";

        /// <summary>
        /// Gets and sets the current context.
        /// </summary>
        public static TestExecutionContext CurrentContext
        {
            // This getter invokes security critical members on the 'System.Runtime.Remoting.Messaging.CallContext' class. 
            // Callers of this method have no influence on how these methods are used so we define a 'SecuritySafeCriticalAttribute' 
            // rather than a 'SecurityCriticalAttribute' to enable use by security transparent callers.
            [SecuritySafeCritical]
            get
            {
                return CallContext.GetData(CONTEXT_KEY) as TestExecutionContext;
            }
            // This setter invokes security critical members on the 'System.Runtime.Remoting.Messaging.CallContext' class. 
            // Callers of this method have no influence on how these methods are used so we define a 'SecuritySafeCriticalAttribute' 
            // rather than a 'SecurityCriticalAttribute' to enable use by security transparent callers.
            [SecuritySafeCritical]
            private set
            {
                if (value == null)
                    CallContext.FreeNamedDataSlot(CONTEXT_KEY);
                else
                    CallContext.SetData(CONTEXT_KEY, value);
            }
        }
#endif

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the current test
        /// </summary>
        public Test CurrentTest { get; set; }

        /// <summary>
        /// The time the current test started execution
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// The time the current test started in Ticks
        /// </summary>
        public long StartTicks { get; set; }

        /// <summary>
        /// Gets or sets the current test result
        /// </summary>
        public TestResult CurrentResult
        {
            get { return _currentResult; }
            set
            {
                _currentResult = value;
                if (value != null)
                    OutWriter = value.OutWriter;
            }
        }

        /// <summary>
        /// Gets a TextWriter that will send output to the current test result.
        /// </summary>
        public TextWriter OutWriter { get; private set; }

        /// <summary>
        /// The current test object - that is the user fixture
        /// object on which tests are being executed.
        /// </summary>
        public object TestObject { get; set; }

        /// <summary>
        /// Get or set indicator that run should stop on the first error
        /// </summary>
        public bool StopOnError { get; set; }

        /// <summary>
        /// Gets an enum indicating whether a stop has been requested.
        /// </summary>
        public TestExecutionStatus ExecutionStatus
        {
            get
            {
                // ExecutionStatus may have been set to StopRequested or AbortRequested
                // in a prior context. If so, reflect the same setting in this context.
                if (_executionStatus == TestExecutionStatus.Running && _priorContext != null)
                    _executionStatus = _priorContext.ExecutionStatus;

                return _executionStatus;
            }
            set
            {
                _executionStatus = value;

                // Push the same setting up to all prior contexts
                if (_priorContext != null)
                    _priorContext.ExecutionStatus = value;
            }
        }

        /// <summary>
        /// The current test event listener
        /// </summary>
        internal ITestListener Listener
        {
            get { return _listener; }
            set { _listener = value; }
        }

        /// <summary>
        /// The current WorkItemDispatcher. Made public for 
        /// use by nunitlite.tests
        /// </summary>
        public IWorkItemDispatcher Dispatcher { get; set; }

        /// <summary>
        /// The ParallelScope to be used by tests running in this context.
        /// For builds with out the parallel feature, it has no effect.
        /// </summary>
        public ParallelScope ParallelScope { get; set; } 

        /// <summary>
        /// Default tolerance value used for floating point equality
        /// when no other tolerance is specified.
        /// </summary>
        public Tolerance DefaultFloatingPointTolerance { get; set; }

#if PARALLEL
        /// <summary>
        /// The worker that spawned the context.
        /// For builds without the parallel feature, it is null.
        /// </summary>
        public TestWorker TestWorker {get; internal set;}
#endif

        /// <summary>
        /// Gets the RandomGenerator specific to this Test
        /// </summary>
        public Randomizer RandomGenerator
        {
            get
            {
                if (_randomGenerator == null)
                    _randomGenerator = new Randomizer(CurrentTest.Seed);
                return _randomGenerator;
            }
        }

        /// <summary>
        /// Gets the assert count.
        /// </summary>
        /// <value>The assert count.</value>
        internal int AssertCount
        {
            get { return _assertCount; }
        }

        /// <summary>
        /// The current nesting level of multiple assert blocks
        /// </summary>
        internal int MultipleAssertLevel { get; set; }

        /// <summary>
        /// Gets or sets the test case timeout value
        /// </summary>
        public int TestCaseTimeout { get; set; }

        /// <summary>
        /// Gets a list of ITestActions set by upstream tests
        /// </summary>
        public List<ITestAction> UpstreamActions { get; private set; }

        // TODO: Put in checks on all of these settings
        // with side effects so we only change them
        // if the value is different

        /// <summary>
        /// Saves or restores the CurrentCulture
        /// </summary>
        public CultureInfo CurrentCulture
        {
            get { return _currentCulture; }
            set
            {
                _currentCulture = value;
#if !PORTABLE && !NETSTANDARD1_6
                Thread.CurrentThread.CurrentCulture = _currentCulture;
#endif
            }
        }

        /// <summary>
        /// Saves or restores the CurrentUICulture
        /// </summary>
        public CultureInfo CurrentUICulture
        {
            get { return _currentUICulture; }
            set
            {
                _currentUICulture = value;
#if !PORTABLE && !NETSTANDARD1_6
                Thread.CurrentThread.CurrentUICulture = _currentUICulture;
#endif
            }
        }

#if !PORTABLE && !NETSTANDARD1_6
        /// <summary>
        /// Gets or sets the current <see cref="IPrincipal"/> for the Thread.
        /// </summary>
        public IPrincipal CurrentPrincipal
        {
            get { return _currentPrincipal; }
            set
            {
                _currentPrincipal = value;
                Thread.CurrentPrincipal = _currentPrincipal;
            }
        }
#endif

        /// <summary>
        /// The current head of the ValueFormatter chain, copied from MsgUtils.ValueFormatter
        /// </summary>
        public ValueFormatter CurrentValueFormatter { get; private set; }

        internal ReadOnlyCollection<TestAttachment> TestAttachments => new ReadOnlyCollection<TestAttachment>(_testAttachments);

        /// <summary>
        /// If true, all tests must run on the same thread. No new thread may be spawned.
        /// </summary>
        public bool IsSingleThreaded { get; set; }

        #endregion

        #region Instance Methods

        /// <summary>
        /// Record any changes in the environment made by
        /// the test code in the execution context so it
        /// will be passed on to lower level tests.
        /// </summary>
        public void UpdateContextFromEnvironment()
        {
            _currentCulture = CultureInfo.CurrentCulture;
            _currentUICulture = CultureInfo.CurrentUICulture;

#if !PORTABLE && !NETSTANDARD1_6
            _currentPrincipal = Thread.CurrentPrincipal;
#endif
        }

        /// <summary>
        /// Set up the execution environment to match a context.
        /// Note that we may be running on the same thread where the
        /// context was initially created or on a different thread.
        /// </summary>
        public void EstablishExecutionEnvironment()
        {
#if !PORTABLE && !NETSTANDARD1_6
            Thread.CurrentThread.CurrentCulture = _currentCulture;
            Thread.CurrentThread.CurrentUICulture = _currentUICulture;
            Thread.CurrentPrincipal = _currentPrincipal;
#endif

            CurrentContext = this;
        }

        /// <summary>
        /// Increments the assert count by one.
        /// </summary>
        public void IncrementAssertCount()
        {
            Interlocked.Increment(ref _assertCount);
        }

        /// <summary>
        /// Increments the assert count by a specified amount.
        /// </summary>
        public void IncrementAssertCount(int count)
        {
            // TODO: Temporary implementation
            while (count-- > 0)
                Interlocked.Increment(ref _assertCount);
        }

        /// <summary>
        /// Adds a new ValueFormatterFactory to the chain of formatters
        /// </summary>
        /// <param name="formatterFactory">The new factory</param>
        public void AddFormatter(ValueFormatterFactory formatterFactory)
        {
            CurrentValueFormatter = formatterFactory(CurrentValueFormatter);
        }

        /// <summary>
        /// Attach a file to the current test result
        /// </summary>
        /// <param name="filepath">Filepath to attachment. Can be absolute, or relative to working directory.</param>
        /// <param name="description">Optional description of attachment</param>
        public void AddTestAttachment(string filepath, string description = null)
        {
            Guard.ArgumentNotNull(filepath, nameof(filepath));
            Guard.ArgumentValid(filepath.IndexOfAny(Path.GetInvalidPathChars()) == -1, 
                $"Test attachment filepath contains invalid path characters. {filepath}", nameof(filepath));

            if (!Path.IsPathRooted(filepath))
                filepath = Path.Combine(TestContext.CurrentContext.WorkDirectory, filepath);

#if !PORTABLE
            if (!File.Exists(filepath))
                throw new FileNotFoundException("Test attachment filepath could not be found.", filepath);
#endif

            _testAttachments.Add(new TestAttachment(filepath, description));
        }

        private TestExecutionContext CreateIsolatedContext()
        {
            var context = new TestExecutionContext(this);

            if (context.CurrentTest != null)
                context.CurrentResult = context.CurrentTest.MakeTestResult();

            return context;
        }

        #endregion

        #region InitializeLifetimeService

#if !PORTABLE && !NETSTANDARD1_6
        /// <summary>
        /// Obtain lifetime service object
        /// </summary>
        /// <returns></returns>
        [SecurityCritical]  // Override of security critical method must be security critical itself
        public override object InitializeLifetimeService()
        {
            return null;
        }
#endif

        #endregion

        #region Nested IsolatedContext Class

        /// <summary>
        /// An IsolatedContext is used when running code
        /// that may effect the current result in ways that 
        /// should not impact the final result of the test.
        /// A new TestExecutionContext is created with an
        /// initially clear result, which is discarded on
        /// exiting the context.
        /// </summary>
        /// <example>
        ///     using (new TestExecutionContext.IsolatedContext())
        ///     {
        ///         // Code that should not impact the result
        ///     }
        /// </example>
        public class IsolatedContext : IDisposable
        {
            private TestExecutionContext _originalContext;

            /// <summary>
            /// Save the original current TestExecutionContext and
            /// make a new isolated context current.
            /// </summary>
            public IsolatedContext()
            {
                _originalContext = CurrentContext;
                CurrentContext = _originalContext.CreateIsolatedContext();
            }

            /// <summary>
            /// Restore the original TestExecutionContext.
            /// </summary>
            public void Dispose()
            {
                CurrentContext = _originalContext;
            }
        }

        #endregion
    }
}
